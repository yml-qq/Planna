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

internal static class HttpFocusPageSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'focus-page@example.test')", owner);
        var session = Guid.NewGuid();
        await AddSession(db, owner, session);
        for (var i = 1; i <= 3; i++)
        {
            await Exec(db, """
                INSERT INTO focus_sessions(owner_id, started_at, planned_seconds, actual_seconds,
                    state, version)
                VALUES($1, now() - make_interval(hours => $2), 1500, $3, 'completed', 1)
                """, owner, i, i * 60);
        }

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new PageFactory();
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(owner, session);

        Check(await Get(client, null, "limit=2"), HttpStatusCode.Unauthorized);

        var first = await Get(client, token, "limit=2");
        Check(first, HttpStatusCode.OK);
        string? cursor;
        using (var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("items").GetArrayLength() != 2)
                throw new InvalidOperationException("Focus page size mismatch.");
            cursor = root.GetProperty("next_cursor").GetString();
            if (string.IsNullOrEmpty(cursor))
                throw new InvalidOperationException("Focus page cursor missing.");
        }

        var second = await Get(client, token, "limit=2&cursor=" + Uri.EscapeDataString(cursor));
        Check(second, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("items").GetArrayLength() != 1 ||
                root.GetProperty("next_cursor").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Focus page continuation mismatch.");
        }

        var invalid = await Get(client, token, "limit=2&cursor=not-a-cursor");
        Check(invalid, HttpStatusCode.BadRequest);
        using (var document = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("error").GetProperty("code").GetString() != "INVALID_CURSOR")
                throw new InvalidOperationException("Focus page invalid cursor code mismatch.");
        }
        Check(await Get(client, token, "limit=0"), HttpStatusCode.BadRequest);
        Console.WriteLine("PASS: focus history cursor pagination, continuation, invalid cursor and limit validation");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/focus-sessions?" + query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AddSession(NpgsqlConnection db, Guid owner, Guid id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            id, owner, hash);
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

    private sealed class PageFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
