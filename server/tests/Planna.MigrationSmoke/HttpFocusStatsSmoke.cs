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

internal static class HttpFocusStatsSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'focus-stats-one@example.test'),($2,'focus-stats-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, Guid.NewGuid());

        await InsertSession(db, owner, "2026-11-10T10:00:00Z", "completed", 600);
        await InsertSession(db, owner, "2026-11-10T23:30:00Z", "completed", 300);
        await InsertSession(db, owner, "2026-11-11T01:00:00Z", "abandoned", 120);
        await InsertSession(db, foreign, "2026-11-10T12:00:00Z", "completed", 999);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new StatsFactory();
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(owner, ownerSession);
        var foreignToken = factory.Services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(foreign, Guid.NewGuid());

        Check(await Get(client, null, "from=2026-11-10&to=2026-11-11"), HttpStatusCode.Unauthorized);

        var utc = await Get(client, token, "from=2026-11-10&to=2026-11-11&time_zone=UTC");
        Check(utc, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await utc.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("total_sessions").GetInt32() != 3 ||
                root.GetProperty("completed_sessions").GetInt32() != 2 ||
                root.GetProperty("abandoned_sessions").GetInt32() != 1 ||
                root.GetProperty("actual_seconds").GetInt64() != 1020)
                throw new InvalidOperationException("Focus stats totals mismatch.");
            var days = root.GetProperty("days");
            if (days.GetArrayLength() != 2 ||
                days[0].GetProperty("date").GetString() != "2026-11-10" ||
                days[0].GetProperty("completed_sessions").GetInt32() != 2 ||
                days[0].GetProperty("actual_seconds").GetInt64() != 900 ||
                days[1].GetProperty("date").GetString() != "2026-11-11" ||
                days[1].GetProperty("actual_seconds").GetInt64() != 120)
                throw new InvalidOperationException("Focus stats UTC days mismatch.");
        }

        var shanghai = await Get(client, token,
            "from=2026-11-10&to=2026-11-11&time_zone=Asia%2FShanghai");
        Check(shanghai, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await shanghai.Content.ReadAsStringAsync()))
        {
            var days = document.RootElement.GetProperty("days");
            if (days.GetArrayLength() != 2 ||
                days[0].GetProperty("date").GetString() != "2026-11-10" ||
                days[0].GetProperty("actual_seconds").GetInt64() != 600 ||
                days[1].GetProperty("date").GetString() != "2026-11-11" ||
                days[1].GetProperty("completed_sessions").GetInt32() != 1 ||
                days[1].GetProperty("actual_seconds").GetInt64() != 420)
                throw new InvalidOperationException("Focus stats zoned days mismatch.");
        }

        var foreignStats = await Get(client, foreignToken, "from=2026-11-10&to=2026-11-11&time_zone=UTC");
        Check(foreignStats, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await foreignStats.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("total_sessions").GetInt32() != 1 ||
                document.RootElement.GetProperty("actual_seconds").GetInt64() != 999)
                throw new InvalidOperationException("Foreign focus stats isolation mismatch.");
        }

        await AssertStatus(client, token, "from=2026-11-11&to=2026-11-10", HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, "from=2026-11-01&to=2027-03-01", HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, "from=2026-11-10&to=2026-11-11&time_zone=Mars%2FBase",
            HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, "to=2026-11-11", HttpStatusCode.BadRequest);
        await AssertStatus(client, token, "from=2026-11-10&to=2026-11-11&time_zone=Asia%2FShanghai",
            HttpStatusCode.OK);
        Console.WriteLine("PASS: focus stats aggregation, time-zone grouping, range/time-zone validation and owner isolation");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/focus-sessions/stats?" + query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AssertStatus(HttpClient client, string token, string query,
        HttpStatusCode expected)
    {
        var response = await Get(client, token, query);
        Check(response, expected);
    }

    private static async Task InsertSession(NpgsqlConnection db, Guid owner, string startedAt,
        string state, int actualSeconds)
    {
        await Exec(db, """
            INSERT INTO focus_sessions(id, owner_id, started_at, ended_at, planned_seconds,
                actual_seconds, break_seconds, state, version)
            VALUES($1, $2, $3::timestamptz, $3::timestamptz + make_interval(secs => $5), 1500, $5, 0, $4, 1)
            """, Guid.NewGuid(), owner, startedAt, state, actualSeconds);
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
            throw new InvalidOperationException(
                $"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private sealed class StatsFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
