using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;

internal static class HttpAuthMatrixSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'auth-matrix@example.test')", owner);
        var session = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            session, owner, hash);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("AI_PROVIDER", "rule_based");
        await using var factory = new MatrixFactory();
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(owner, session);

        var id = Guid.NewGuid();
        var cases = new (HttpMethod Method, string Path, string? Body)[]
        {
            (HttpMethod.Get, "/tasks", null),
            (HttpMethod.Get, "/lists", null),
            (HttpMethod.Get, "/tags", null),
            (HttpMethod.Get, "/tasks/" + id + "/steps", null),
            (HttpMethod.Get, "/sync/pull", null),
            (HttpMethod.Get, "/habits", null),
            (HttpMethod.Get, "/focus-sessions", null),
            (HttpMethod.Get, "/goals", null),
            (HttpMethod.Get, "/notes", null),
            (HttpMethod.Get, "/ai/consent", null),
            (HttpMethod.Get, "/me/exports", null),
            (HttpMethod.Get, "/auth/devices", null),
            (HttpMethod.Get, "/reminders/upcoming", null),
            (HttpMethod.Get, "/tasks/" + id + "/reminders", null),
            (HttpMethod.Get, "/tasks/" + id + "/recurrence", null),
            (HttpMethod.Get, "/tasks/" + id + "/occurrences", null),
            (HttpMethod.Post, "/tasks", "{\"title\":\"x\"}"),
            (HttpMethod.Post, "/sync/push", "{}"),
            (HttpMethod.Post, "/notes", "{\"content\":\"x\"}"),
            (HttpMethod.Post, "/goals", "{\"title\":\"x\"}"),
            (HttpMethod.Post, "/focus-sessions", "{}"),
            (HttpMethod.Post, "/habits", "{}"),
            (HttpMethod.Post, "/tasks/" + id + "/reminders", "{}"),
            (HttpMethod.Patch, "/notes/" + id, "{}"),
            (HttpMethod.Patch, "/goals/" + id, "{}"),
            (HttpMethod.Patch, "/habits/" + id, "{}"),
            (HttpMethod.Patch, "/focus-sessions/" + id, "{}"),
            (HttpMethod.Delete, "/notes/" + id, null),
            (HttpMethod.Delete, "/goals/" + id, null),
            (HttpMethod.Delete, "/habits/" + id, null)
        };
        foreach (var (method, path, body) in cases)
        {
            var response = await Send(client, null, method, path, body);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    $"Anonymous {method} {path} expected 401, got {(int)response.StatusCode}.");
            AssertSecurityHeaders(response);
        }

        var invalid = await Send(client, "not-a-jwt", HttpMethod.Get, "/tasks", null);
        Check(invalid, HttpStatusCode.Unauthorized);

        var health = await client.GetAsync("/health/live");
        Check(health, HttpStatusCode.OK);
        AssertSecurityHeaders(health);

        var authenticated = await Send(client, token, HttpMethod.Get, "/notes", null);
        Check(authenticated, HttpStatusCode.OK);
        Console.WriteLine("PASS: anonymous access matrix returns 401 across tasks/lists/tags/steps/sync/habits/focus/goals/notes/ai/me/reminders; health stays public");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string? token, HttpMethod method,
        string path, string? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        if (response.Headers.GetValues("X-Content-Type-Options").Single() != "nosniff" ||
            response.Headers.GetValues("X-Frame-Options").Single() != "DENY" ||
            response.Headers.GetValues("Referrer-Policy").Single() != "no-referrer")
            throw new InvalidOperationException("Security headers missing on auth matrix response.");
    }

    private static void Check(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private static async Task Exec(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MatrixFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
