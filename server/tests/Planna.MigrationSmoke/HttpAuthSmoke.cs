using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;
using Planna.Application.Tasks;

internal static class HttpAuthSmoke
{
    public static async Task RunAsync(NpgsqlConnection database)
    {
        var sender = new CaptureCodeSender();
        await using var factory = new AuthFactory(sender);
        using var client = factory.CreateClient();
        const string email = "auth-smoke@example.test";
        var key = Guid.NewGuid();

        var challenge = await Challenge(client, email, key);
        Check(challenge, HttpStatusCode.Accepted);
        if (!sender.Codes.TryGetValue(email, out var code))
            throw new InvalidOperationException("Login code was not sent.");
        Check(await Challenge(client, email, key), HttpStatusCode.Accepted);
        if (sender.SendCount != 1) throw new InvalidOperationException("Challenge retry sent duplicate mail.");

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
        sender.FailNext = true;
        Check(await Challenge(client, failedEmail, failureKey), HttpStatusCode.ServiceUnavailable);
        Check(await Challenge(client, failedEmail, failureKey), HttpStatusCode.Accepted);
        if (!sender.Codes.ContainsKey(failedEmail))
            throw new InvalidOperationException("Challenge could not retry after mail failure.");

        const string lockedEmail = "auth-attempts@example.test";
        Check(await Challenge(client, lockedEmail, Guid.NewGuid()), HttpStatusCode.Accepted);
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
        Console.WriteLine("PASS: OTP login, mail retry, attempt limit, refresh rotation and access revocation");
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
        public bool FailNext { get; set; }
        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Synthetic mail failure.");
            }
            Codes[email] = code;
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthFactory(CaptureCodeSender sender) : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILoginCodeSender>();
                services.AddScoped<ILoginCodeSender>(_ => sender);
            });
        }
    }
}
