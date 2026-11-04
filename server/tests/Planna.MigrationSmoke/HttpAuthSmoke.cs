using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;
using Planna.Application.Tasks;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;

internal static class HttpAuthSmoke
{
    public static async Task RunAsync(NpgsqlConnection database)
    {
        var sender = new CaptureCodeSender();
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        const string email = "auth-smoke@example.test";
        var key = Guid.NewGuid();

        var challenge = await Challenge(client, email, key);
        Check(challenge, HttpStatusCode.Accepted);
        if (sender.Codes.ContainsKey(email) || !await Dispatch(factory, sender))
            throw new InvalidOperationException("Challenge was not durably queued.");
        if (!sender.Codes.TryGetValue(email, out var code))
            throw new InvalidOperationException("Login code was not sent.");
        Check(await Challenge(client, email, key), HttpStatusCode.Accepted);
        if (sender.SendCount != 1 || await Dispatch(factory, sender))
            throw new InvalidOperationException("Challenge retry sent duplicate mail.");

        var bad = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest(email, "000000", "Test device", "windows"));
        Check(bad, HttpStatusCode.Unauthorized);
        var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest(email, code, "Test device", "windows"));
        Check(login, HttpStatusCode.OK);
        var tokens = await login.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Missing token response.");
        if (tokens.ExpiresIn != 600 || string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.RefreshToken.Length != 64)
            throw new InvalidOperationException("Invalid login tokens.");
        Check(await client.PostAsJsonAsync("/auth/login",
            new LoginRequest(email, code, "Test device", "windows")), HttpStatusCode.Unauthorized);

        var refresh = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Check(refresh, HttpStatusCode.OK);
        var rotated = await refresh.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Missing rotated tokens.");
        if (rotated.RefreshToken == tokens.RefreshToken)
            throw new InvalidOperationException("Refresh token did not rotate.");
        Check(await TaskProbe(client, rotated.AccessToken), HttpStatusCode.BadRequest);
        Check(await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken)),
            HttpStatusCode.Unauthorized);
        Check(await TaskProbe(client, rotated.AccessToken), HttpStatusCode.Unauthorized);
        Check(await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(rotated.RefreshToken)),
            HttpStatusCode.Unauthorized);

        const string failedEmail = "auth-mail-failure@example.test";
        var failureKey = Guid.NewGuid();
        sender.FailuresRemaining = 1;
        Check(await Challenge(client, failedEmail, failureKey), HttpStatusCode.Accepted);
        if (!await Dispatch(factory, sender) || sender.Codes.ContainsKey(failedEmail))
            throw new InvalidOperationException("Failed delivery was not retained.");
        await using (var pending = new NpgsqlCommand("""
            SELECT count(*) FROM auth_email_outbox AS o
            JOIN auth_login_challenges AS c ON c.id=o.challenge_id
            WHERE c.email=$1 AND o.status='pending' AND o.attempts=1 AND o.ciphertext IS NOT NULL
            """, database))
        {
            pending.Parameters.AddWithValue(failedEmail);
            if ((long)(await pending.ExecuteScalarAsync() ?? 0L) != 1)
                throw new InvalidOperationException("Failed mail lost its encrypted outbox payload.");
        }
        await Execute(database, """
            UPDATE auth_email_outbox SET next_attempt_at=now()-interval '1 second'
            WHERE challenge_id=(SELECT id FROM auth_login_challenges WHERE email=$1)
            """, failedEmail);
        Check(await Challenge(client, failedEmail, failureKey), HttpStatusCode.Accepted);
        if (!await Dispatch(factory, sender) || !sender.Codes.ContainsKey(failedEmail))
            throw new InvalidOperationException("Challenge could not retry after mail failure.");

        const string crashEmail = "auth-crash-retry@example.test";
        Check(await Challenge(client, crashEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        await Execute(database, """
            UPDATE auth_email_outbox SET status='leased', lease_token=$2,
                lease_until=now()-interval '1 second'
            WHERE challenge_id=(SELECT id FROM auth_login_challenges WHERE email=$1)
            """, crashEmail, Guid.NewGuid());
        if (!await Dispatch(factory, sender) || !sender.Codes.ContainsKey(crashEmail))
            throw new InvalidOperationException("Expired lease was not reclaimed.");

        const string concurrentEmail = "auth-concurrent-dispatch@example.test";
        Check(await Challenge(client, concurrentEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        var beforeConcurrent = sender.SendCount;
        await Task.WhenAll(Dispatch(factory, sender), Dispatch(factory, sender));
        if (sender.SendCount != beforeConcurrent + 1)
            throw new InvalidOperationException("Concurrent dispatch sent the same job twice.");

        const string expiredEmail = "auth-expired-delivery@example.test";
        Check(await Challenge(client, expiredEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        await Execute(database, "UPDATE auth_login_challenges SET expires_at=now()-interval '1 second' WHERE email=$1",
            expiredEmail);
        await Dispatch(factory, sender);
        await using (var expired = new NpgsqlCommand("""
            SELECT count(*) FROM auth_email_outbox AS o
            JOIN auth_login_challenges AS c ON c.id=o.challenge_id
            WHERE c.email=$1 AND o.status='expired' AND o.ciphertext IS NULL
            """, database))
        {
            expired.Parameters.AddWithValue(expiredEmail);
            if ((long)(await expired.ExecuteScalarAsync() ?? 0L) != 1 || sender.Codes.ContainsKey(expiredEmail))
                throw new InvalidOperationException("Expired code was sent or retained.");
        }

        const string tamperedEmail = "auth-tampered-delivery@example.test";
        Check(await Challenge(client, tamperedEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        await Execute(database, """
            UPDATE auth_email_outbox SET ciphertext=set_byte(ciphertext,0,get_byte(ciphertext,0) # 1)
            WHERE challenge_id=(SELECT id FROM auth_login_challenges WHERE email=$1)
            """, tamperedEmail);
        if (!await Dispatch(factory, sender) || sender.Codes.ContainsKey(tamperedEmail))
            throw new InvalidOperationException("Tampered payload was accepted.");
        await AssertOutbox(database, tamperedEmail, "failed", 1);

        const string exhaustedEmail = "auth-retry-exhausted@example.test";
        Check(await Challenge(client, exhaustedEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        sender.FailuresRemaining = 5;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!await Dispatch(factory, sender))
                throw new InvalidOperationException("Retry job disappeared early.");
            await Execute(database, """
                UPDATE auth_email_outbox SET next_attempt_at=now()-interval '1 second'
                WHERE challenge_id=(SELECT id FROM auth_login_challenges WHERE email=$1)
                """, exhaustedEmail);
        }
        await AssertOutbox(database, exhaustedEmail, "failed", 5);

        const string lockedEmail = "auth-attempts@example.test";
        Check(await Challenge(client, lockedEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
        if (!await Dispatch(factory, sender)) throw new InvalidOperationException("Attempt-limit code was not sent.");
        for (var attempt = 0; attempt < 5; attempt++)
            Check(await client.PostAsJsonAsync("/auth/login",
                new LoginRequest(lockedEmail, "000000", "Test", "windows")), HttpStatusCode.Unauthorized);
        Check(await client.PostAsJsonAsync("/auth/login",
            new LoginRequest(lockedEmail, sender.Codes[lockedEmail], "Test", "windows")), HttpStatusCode.Unauthorized);

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM users WHERE email=$1 AND deleted_at IS NULL", database);
        count.Parameters.AddWithValue(email);
        if ((long)(await count.ExecuteScalarAsync() ?? 0L) != 1)
            throw new InvalidOperationException("Verified login did not create exactly one account.");
        Console.WriteLine("PASS: OTP login, durable mail retry/crash/concurrency/expiry/tamper, refresh rotation and access revocation");
    }

    private static async Task<bool> Dispatch(AuthFactory factory, ILoginCodeSender sender)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = new LoginEmailDispatcher(scope.ServiceProvider.GetRequiredService<AuthContext>(),
            sender, scope.ServiceProvider.GetRequiredService<LoginCodeCipher>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(), NullLogger<LoginEmailDispatcher>.Instance);
        return await dispatcher.DispatchOneAsync(CancellationToken.None);
    }

    private static async Task Execute(NpgsqlConnection database, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, database);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOutbox(NpgsqlConnection database, string email, string status, int attempts)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM auth_email_outbox AS o
            JOIN auth_login_challenges AS c ON c.id=o.challenge_id
            WHERE c.email=$1 AND o.status=$2 AND o.attempts=$3 AND o.ciphertext IS NULL
            """, database);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(attempts);
        if ((long)(await command.ExecuteScalarAsync() ?? 0L) != 1)
            throw new InvalidOperationException("Terminal outbox job retained a code.");
    }

    private static async Task<HttpResponseMessage> TaskProbe(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks")
        {
            Content = JsonContent.Create(new CreateTaskRequest(Guid.NewGuid(), Guid.NewGuid(),
                "Probe", null, 0, null, null, null, false, "Asia/Shanghai", null))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Challenge(HttpClient client, string email, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login/challenges")
        {
            Content = JsonContent.Create(new LoginChallengeRequest(email))
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static void Check(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private sealed class CaptureCodeSender : ILoginCodeSender
    {
        public Dictionary<string, string> Codes { get; } = [];
        public int SendCount { get; private set; }
        public int FailuresRemaining { get; set; }
        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Synthetic mail failure.");
            }
            Codes[email] = code;
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
        }
    }
}
