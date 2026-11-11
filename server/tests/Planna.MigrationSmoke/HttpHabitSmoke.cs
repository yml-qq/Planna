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

internal static class HttpHabitSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'habit-one@example.test'),($2,'habit-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new HabitFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await List(client, null), HttpStatusCode.Unauthorized);
        Check(await Create(client, null, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);

        await AssertCode(await Create(client, token, Guid.NewGuid(),
            HabitBody("", "daily", null, 1, "count", "Asia/Shanghai")),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT");
        await AssertCode(await Create(client, token, Guid.NewGuid(),
            HabitBody("Weekly", "weekly", null, 1, "count", "Asia/Shanghai")),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT");
        await AssertCode(await Create(client, token, Guid.NewGuid(),
            "{\"title\":\"Bad\",\"schedule_type\":\"daily\",\"time_zone\":\"Not/AZone\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT");
        await AssertCode(await Create(client, token, Guid.NewGuid(),
            HabitBody("Bad", "monthly", null, 1, "count", "Asia/Shanghai")),
            HttpStatusCode.UnprocessableEntity, "INVALID_HABIT");

        // Create with idempotent replay, and a weekly habit with sorted weekdays.
        var firstKey = Guid.NewGuid();
        var firstBody = HabitBody("Read", "daily", null, 1, "count", "Asia/Shanghai");
        var created = await Create(client, token, firstKey, firstBody);
        Check(created, HttpStatusCode.Created);
        var first = await created.Content.ReadFromJsonAsync<HabitView>()
            ?? throw new InvalidOperationException("Habit missing.");
        if (first.Version != 1 || first.Weekdays is not null || first.TargetQuantity != 1 ||
            first.Unit != "count" || first.PausedAt is not null)
            throw new InvalidOperationException("Habit defaults mismatch.");
        var replay = await Create(client, token, firstKey, firstBody);
        Check(replay, HttpStatusCode.Created);
        if ((await replay.Content.ReadFromJsonAsync<HabitView>())?.Id != first.Id)
            throw new InvalidOperationException("Habit create replay changed id.");
        var second = await (await Create(client, token, Guid.NewGuid(),
            HabitBody("Run", "weekly", "[5,1,3]", 2, "km", "Asia/Shanghai")))
            .Content.ReadFromJsonAsync<HabitView>()
            ?? throw new InvalidOperationException("Weekly habit missing.");
        if (second.Weekdays is null || !second.Weekdays.SequenceEqual(new short[] { 1, 3, 5 }))
            throw new InvalidOperationException("Weekdays were not normalized.");

        var list = await (await List(client, token)).Content.ReadFromJsonAsync<HabitList>()
            ?? throw new InvalidOperationException("Habit list missing.");
        if (list.Items.Count != 2) throw new InvalidOperationException("Habit list mismatch.");
        Check(await Get(client, token, first.Id), HttpStatusCode.OK);
        Check(await Get(client, token, Guid.NewGuid()), HttpStatusCode.NotFound);
        Check(await Get(client, foreignToken, first.Id), HttpStatusCode.NotFound);

        // Version conflict, patch and replay.
        await AssertCode(await Patch(client, token, first.Id, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"title\":\"Read more\"}}"),
            HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var patchKey = Guid.NewGuid();
        var patchBody = "{\"expected_version\":1,\"patch\":{\"title\":\"Read more\",\"target_quantity\":20,\"paused\":true}}";
        var patched = await Patch(client, token, first.Id, patchKey, patchBody);
        Check(patched, HttpStatusCode.OK);
        var updated = await patched.Content.ReadFromJsonAsync<HabitView>()
            ?? throw new InvalidOperationException("Patched habit missing.");
        if (updated.Version != 2 || updated.Title != "Read more" || updated.TargetQuantity != 20 ||
            updated.PausedAt is null)
            throw new InvalidOperationException("Habit patch projection mismatch.");
        var patchReplay = await Patch(client, token, first.Id, patchKey, patchBody);
        Check(patchReplay, HttpStatusCode.OK);
        if ((await patchReplay.Content.ReadFromJsonAsync<HabitView>())?.Version != 2)
            throw new InvalidOperationException("Habit patch replay mismatch.");
        var daily = await (await Patch(client, token, second.Id, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"schedule_type\":\"daily\",\"weekdays\":[2]}}"))
            .Content.ReadFromJsonAsync<HabitView>()
            ?? throw new InvalidOperationException("Daily habit missing.");
        if (daily.Weekdays is not null) throw new InvalidOperationException("Weekdays were not cleared.");

        // Delete with If-Match, replay and stale version.
        await AssertCode(await Delete(client, token, first.Id, "v1", Guid.NewGuid()),
            HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var deleteKey = Guid.NewGuid();
        Check(await Delete(client, token, first.Id, "v2", deleteKey), HttpStatusCode.NoContent);
        Check(await Delete(client, token, first.Id, "v2", deleteKey), HttpStatusCode.NoContent);
        await AssertCode(await Delete(client, token, first.Id, "v2", Guid.NewGuid()),
            HttpStatusCode.NotFound, "HABIT_NOT_FOUND");

        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await List(client, foreignToken), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Habits are server-only here: no revision/sync changes, mutations stay idempotent.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM habits WHERE owner_id=$1 AND deleted_at IS NULL", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM habits WHERE owner_id=$1 AND deleted_at IS NOT NULL", owner) != 1)
            throw new InvalidOperationException("Habit writes did not keep idempotency and sync isolation consistent.");
        Console.WriteLine("PASS: habit create/list/get/patch/delete, validation, idempotency, versions, owner isolation");
    }

    private static string HabitBody(string title, string schedule, string? weekdays, int target, string unit,
        string timeZone) =>
        "{\"title\":\"" + title + "\",\"schedule_type\":\"" + schedule + "\"" +
        (weekdays is null ? "" : ",\"weekdays\":" + weekdays) +
        ",\"target_quantity\":" + target + ",\"unit\":\"" + unit + "\",\"time_zone\":\"" + timeZone + "\"}";

    private static async Task<HttpResponseMessage> List(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/habits");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/habits/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Create(HttpClient client, string? token, Guid key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/habits")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Patch(HttpClient client, string? token, Guid id, Guid key,
        string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/habits/" + id)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string? token, Guid id,
        string version, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/habits/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
        request.Headers.Add("Idempotency-Key", key.ToString());
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

    private sealed class HabitFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
