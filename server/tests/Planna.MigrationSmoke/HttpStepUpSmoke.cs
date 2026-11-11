using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;

internal static class HttpStepUpSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var email = "step-up-smoke@example.test";
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,$2)", owner, email);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1)", owner);
        var session = Guid.NewGuid();
        var sessionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            session, owner, sessionHash);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        var sender = new CaptureSender();
        await using var factory = new StepUpFactory(sender);
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, session);

        Check(await Challenge(client, null, Guid.NewGuid()), HttpStatusCode.Unauthorized);
        Check(await Verify(client, null, "account_delete", "000000"), HttpStatusCode.Unauthorized);

        // Challenge, durable queueing, idempotent replay and single delivery.
        var challengeKey = Guid.NewGuid();
        var challenge = await Challenge(client, token, challengeKey);
        Check(challenge, HttpStatusCode.Accepted);
        var challengeView = await challenge.Content.ReadFromJsonAsync<StepUpChallengeResponse>()
            ?? throw new InvalidOperationException("Step-up challenge missing.");
        if (challengeView.Purpose != "account_delete" || challengeView.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Step-up challenge projection mismatch.");
        if (!await Dispatch(factory, sender) || sender.Codes.Count != 1)
            throw new InvalidOperationException("Step-up challenge was not durably queued.");
        var firstCode = sender.Codes[0];
        var replay = await Challenge(client, token, challengeKey);
        Check(replay, HttpStatusCode.Accepted);
        if ((await replay.Content.ReadFromJsonAsync<StepUpChallengeResponse>())?.ExpiresAt != challengeView.ExpiresAt)
            throw new InvalidOperationException("Step-up challenge replay changed expiry.");
        if (await Dispatch(factory, sender) || sender.Codes.Count != 1)
            throw new InvalidOperationException("Step-up challenge replay sent another mail.");

        await AssertCode(await Verify(client, token, "account_delete", "000000"),
            HttpStatusCode.Unauthorized, "INVALID_STEP_UP");
        var verified = await Verify(client, token, "account_delete", firstCode);
        Check(verified, HttpStatusCode.OK);
        var tokenView = await verified.Content.ReadFromJsonAsync<StepUpVerifyResponse>()
            ?? throw new InvalidOperationException("Step-up token missing.");
        if (tokenView.ConfirmationToken.Length < 16 || tokenView.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Step-up token projection mismatch.");
        await AssertCode(await Verify(client, token, "account_delete", firstCode),
            HttpStatusCode.Unauthorized, "INVALID_STEP_UP");

        // The confirmation token is bound to this owner and purpose.
        var pepper = Environment.GetEnvironmentVariable("AUTH_CODE_PEPPER")!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = new StepUpService(scope.ServiceProvider.GetRequiredService<AuthContext>(),
                scope.ServiceProvider.GetRequiredService<LoginCodeCipher>(), TimeProvider.System, pepper);
            if (!service.TryValidateToken(owner, "account_delete", tokenView.ConfirmationToken) ||
                service.TryValidateToken(Guid.NewGuid(), "account_delete", tokenView.ConfirmationToken) ||
                service.TryValidateToken(owner, "other_purpose", tokenView.ConfirmationToken) ||
                service.TryValidateToken(owner, "account_delete", tokenView.ConfirmationToken + "x"))
                throw new InvalidOperationException("Step-up token validation mismatch.");
        }

        // Expired challenges are rejected even with the correct code.
        Check(await Challenge(client, token, Guid.NewGuid()), HttpStatusCode.Accepted);
        if (!await Dispatch(factory, sender) || sender.Codes.Count != 2)
            throw new InvalidOperationException("Second step-up code was not sent.");
        await Exec(db, "UPDATE auth_step_up_challenges SET expires_at=now()-interval '1 minute' WHERE user_id=$1 AND consumed_at IS NULL", owner);
        await AssertCode(await Verify(client, token, "account_delete", sender.Codes[1]),
            HttpStatusCode.Unauthorized, "INVALID_STEP_UP");

        // Attempt limit and challenge rate limit.
        Check(await Challenge(client, token, Guid.NewGuid()), HttpStatusCode.Accepted);
        for (var attempt = 0; attempt < 5; attempt++)
            await AssertCode(await Verify(client, token, "account_delete", "000000"),
                HttpStatusCode.Unauthorized, "INVALID_STEP_UP");
        await AssertCode(await Verify(client, token, "account_delete", "000000"),
            HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        await AssertCode(await Challenge(client, token, Guid.NewGuid()),
            HttpStatusCode.TooManyRequests, "RATE_LIMITED");

        // Deleted accounts cannot start a step-up challenge.
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", owner);
        var deleted = await Challenge(client, token, Guid.NewGuid());
        Check(deleted, HttpStatusCode.Unauthorized);
        using (var json = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync()))
        {
            if (json.RootElement.GetProperty("error").GetProperty("code").GetString() != "ACCOUNT_DELETED")
                throw new InvalidOperationException("Deleted account step-up error mismatch.");
        }

        // Step-up is an auth side effect: no sync revision, changes or mutation records.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM auth_step_up_challenges WHERE user_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM auth_step_up_email_outbox AS o JOIN auth_step_up_challenges AS c ON c.id=o.challenge_id WHERE c.user_id=$1 AND o.status='sent'", owner) != 2)
            throw new InvalidOperationException("Step-up writes did not stay out of the sync log.");
        Console.WriteLine("PASS: step-up challenge queue, replay, token binding, expiry, attempt/rate limits, no sync writes");
    }

    private static async Task<HttpResponseMessage> Challenge(HttpClient client, string? token, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/step-up/challenges");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Verify(HttpClient client, string? token, string purpose, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/step-up")
        {
            Content = new StringContent("{\"purpose\":\"" + purpose + "\",\"code\":\"" + code + "\"}",
                Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<bool> Dispatch(StepUpFactory factory, ILoginCodeSender sender)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = new StepUpEmailDispatcher(
            scope.ServiceProvider.GetRequiredService<AuthContext>(), sender,
            scope.ServiceProvider.GetRequiredService<LoginCodeCipher>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            NullLogger<StepUpEmailDispatcher>.Instance);
        return await dispatcher.DispatchOneAsync(CancellationToken.None);
    }

    private static async Task<long> Scalar(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private static async Task Exec(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static void Check(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private static async Task AssertCode(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Check(response, expected);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (json.RootElement.GetProperty("error").GetProperty("code").GetString() != code ||
            !json.RootElement.TryGetProperty("request_id", out _))
            throw new InvalidOperationException($"Expected error code {code}.");
    }

    private sealed class CaptureSender : ILoginCodeSender
    {
        public List<string> Codes { get; } = [];

        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            Codes.Add(code);
            return Task.CompletedTask;
        }
    }

    private sealed class StepUpFactory(ILoginCodeSender sender) : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton(sender));
        }
    }
}
