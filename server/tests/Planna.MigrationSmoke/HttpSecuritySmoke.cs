using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;

internal static class HttpSecuritySmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'security-smoke@example.test')", owner);
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
        Environment.SetEnvironmentVariable("AI_PROVIDER", "rule_based");
        Environment.SetEnvironmentVariable("AI_RATE_LIMIT_PER_MINUTE", "3");
        await using var factory = new SecurityFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, session);

        // Baseline security headers on success and on errors.
        var health = await client.GetAsync("/health/live");
        Check(health, HttpStatusCode.OK);
        AssertHeaders(health);
        var unauthorized = await client.GetAsync("/me/exports");
        Check(unauthorized, HttpStatusCode.Unauthorized);
        AssertHeaders(unauthorized);
        AssertNoStackTrace(await unauthorized.Content.ReadAsStringAsync());

        // Oversized JSON bodies are rejected before any handler runs.
        var oversizedBody = "{\"title\":\"" + new string('a', 1_100_000) + "\"}";
        var oversized = await PostTasks(client, token, Guid.NewGuid(), oversizedBody);
        await AssertCode(oversized, HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        AssertHeaders(oversized);
        AssertNoStackTrace(await oversized.Content.ReadAsStringAsync());

        // AI endpoints are rate limited per owner (test limit = 3/min).
        for (var i = 0; i < 3; i++)
        {
            var ok = await Parse(client, token);
            Check(ok, HttpStatusCode.OK);
        }
        var limited = await Parse(client, token);
        await AssertCode(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        if (limited.Headers.RetryAfter?.Delta != TimeSpan.FromSeconds(60))
            throw new InvalidOperationException("Rate limit response is missing Retry-After.");
        AssertHeaders(limited);
        AssertNoStackTrace(await limited.Content.ReadAsStringAsync());

        Environment.SetEnvironmentVariable("AI_RATE_LIMIT_PER_MINUTE", "60");
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 0)
            throw new InvalidOperationException("Rejected security requests mutated business state.");
        Console.WriteLine("PASS: security headers, safe error bodies, request size limit, per-owner AI rate limiting");
    }

    private static async Task<HttpResponseMessage> PostTasks(HttpClient client, string token, Guid key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Parse(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/parse")
        {
            Content = new StringContent(
                "{\"input\":\"买牛奶\",\"context\":{\"time_zone\":\"Asia/Shanghai\",\"now\":\"2026-11-11T12:00:00Z\"}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        if (response.Headers.GetValues("X-Content-Type-Options").Single() != "nosniff" ||
            response.Headers.GetValues("X-Frame-Options").Single() != "DENY" ||
            response.Headers.GetValues("Referrer-Policy").Single() != "no-referrer" ||
            response.Headers.CacheControl?.NoStore != true)
            throw new InvalidOperationException("Security headers are missing.");
    }

    private static void AssertNoStackTrace(string body)
    {
        if (body.Contains("   at ", StringComparison.Ordinal) || body.Contains("Exception", StringComparison.Ordinal))
            throw new InvalidOperationException("Error body leaked internal diagnostics.");
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

    private sealed class SecurityFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
