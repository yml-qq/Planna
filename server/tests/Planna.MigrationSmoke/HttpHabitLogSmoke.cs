using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planna.Api;
using Planna.Application.Habits;
using Planna.Application.Identity;

internal static class HttpHabitLogSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var habit = Guid.NewGuid();
        var foreignHabit = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'habit-log-one@example.test'),($2,'habit-log-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO habits(id,owner_id,title,schedule_type,time_zone) VALUES($1,$2,'Read','daily','Asia/Shanghai'),($3,$4,'Foreign','daily','Asia/Shanghai')",
            habit, owner, foreignHabit, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new LogFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Put(client, null, habit, "2026-11-10", Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await GetLogs(client, null, habit, "2026-11-01", "2026-11-30"), HttpStatusCode.Unauthorized);

        await AssertCode(await Put(client, token, habit, "2026-13-01", Guid.NewGuid(), "{}"),
            HttpStatusCode.BadRequest, "INVALID_DATE");
        await AssertCode(await Put(client, token, Guid.NewGuid(), "2026-11-10", Guid.NewGuid(), "{}"),
            HttpStatusCode.NotFound, "HABIT_NOT_FOUND");
        await AssertCode(await Put(client, token, habit, "2026-11-10", Guid.NewGuid(),
            "{\"quantity\":1,\"status\":\"unknown\"}"), HttpStatusCode.UnprocessableEntity, "INVALID_HABIT_LOG");
        await AssertCode(await Put(client, token, habit, "2026-11-10", Guid.NewGuid(),
            "{\"quantity\":1001,\"status\":\"done\"}"), HttpStatusCode.UnprocessableEntity, "INVALID_HABIT_LOG");

        // Same-date upsert keeps one row and bumps the version; replay is idempotent.
        var firstKey = Guid.NewGuid();
        var created = await Put(client, token, habit, "2026-11-10", firstKey,
            "{\"quantity\":5,\"status\":\"done\"}");
        Check(created, HttpStatusCode.OK);
        var first = await created.Content.ReadFromJsonAsync<HabitLogView>()
            ?? throw new InvalidOperationException("Habit log missing.");
        if (first.Version != 1 || first.Quantity != 5 || first.Status != "done")
            throw new InvalidOperationException("Habit log projection mismatch.");
        var replay = await Put(client, token, habit, "2026-11-10", firstKey,
            "{\"quantity\":5,\"status\":\"done\"}");
        Check(replay, HttpStatusCode.OK);
        if ((await replay.Content.ReadFromJsonAsync<HabitLogView>())?.Id != first.Id)
            throw new InvalidOperationException("Habit log replay changed id.");
        var updated = await Put(client, token, habit, "2026-11-10", Guid.NewGuid(),
            "{\"quantity\":1,\"status\":\"skipped\"}");
        Check(updated, HttpStatusCode.OK);
        var second = await updated.Content.ReadFromJsonAsync<HabitLogView>()
            ?? throw new InvalidOperationException("Habit log update missing.");
        if (second.Id != first.Id || second.Version != 2 || second.Status != "skipped" || second.Quantity != 1)
            throw new InvalidOperationException("Habit log upsert did not update in place.");
        var backfill = await Put(client, token, habit, "2026-11-12", Guid.NewGuid(), "{\"quantity\":3}");
        Check(backfill, HttpStatusCode.OK);
        if ((await backfill.Content.ReadFromJsonAsync<HabitLogView>())?.Version != 1)
            throw new InvalidOperationException("Habit log backfill mismatch.");

        var list = await (await GetLogs(client, token, habit, "2026-11-01", "2026-11-30"))
            .Content.ReadFromJsonAsync<HabitLogList>()
            ?? throw new InvalidOperationException("Habit log list missing.");
        if (list.Items.Count != 2 || !list.Items.Select(x => x.LogDate).SequenceEqual(
                new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12) }))
            throw new InvalidOperationException("Habit log list mismatch.");
        await AssertCode(await GetLogs(client, token, habit, "2026-11-01", "2027-12-31"),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT_LOG_RANGE");
        await AssertCode(await GetLogs(client, token, habit, "2026-11-30", "2026-11-01"),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT_LOG_RANGE");
        Check(await GetLogsWithoutQuery(client, token, habit), HttpStatusCode.BadRequest);

        await AssertCode(await GetLogs(client, foreignToken, habit, "2026-11-01", "2026-11-30"),
            HttpStatusCode.NotFound, "HABIT_NOT_FOUND");
        await AssertCode(await Put(client, foreignToken, habit, "2026-11-10", Guid.NewGuid(), "{}"),
            HttpStatusCode.NotFound, "HABIT_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await GetLogs(client, foreignToken, foreignHabit, "2026-11-01", "2026-11-30"),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Logs are server-only: no revision/sync changes; idempotency records match successful writes.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM habit_logs WHERE owner_id=$1", owner) != 2)
            throw new InvalidOperationException("Habit log writes broke sync isolation or idempotency accounting.");
        Console.WriteLine("PASS: habit log upsert by date, replay, backfill, window listing, owner isolation");
    }

    private static async Task<HttpResponseMessage> Put(HttpClient client, string? token, Guid habitId,
        string date, Guid key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/habits/" + habitId + "/logs/" + date)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetLogs(HttpClient client, string? token, Guid habitId,
        string from, string to)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/habits/" + habitId + "/logs?from=" + from + "&to=" + to);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetLogsWithoutQuery(HttpClient client, string token, Guid habitId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/habits/" + habitId + "/logs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AddSession(NpgsqlConnection db, Guid owner, Guid id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            id, owner, hash);
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

    private sealed class LogFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
