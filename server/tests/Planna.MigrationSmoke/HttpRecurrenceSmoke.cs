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
using Planna.Application.Identity;
using Planna.Application.Tasks;

internal static class HttpRecurrenceSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var tF = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-rec-one@example.test'),($2,'http-rec-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'F','custom')",
            listA, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'T1'),($4,$2,$3,'T2'),($5,$2,$3,'T3')",
            t1, owner, listA, t2, t3);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'TF')", tF, foreign, foreignList);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new RecurrenceFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and empty rule reads.
        Check(await GetRecurrence(client, null, t1), HttpStatusCode.Unauthorized);
        Check(await PutRecurrence(client, null, t1, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await DeleteRecurrence(client, null, t1, Guid.NewGuid(), "\"v1\""), HttpStatusCode.Unauthorized);
        Check(await GetOccurrencesRaw(client, null, t1, "2026-11-09", "2026-11-22"), HttpStatusCode.Unauthorized);
        await AssertCode(await GetRecurrence(client, token, t1), HttpStatusCode.NotFound, "RECURRENCE_NOT_FOUND");

        // Rule validation.
        await AssertCode(await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(0, "after_completion", "FREQ=DAILY", "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "UNSUPPORTED_RECURRENCE");
        await AssertCode(await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(0, "rrule", "FREQ=HOURLY", "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "INVALID_RECURRENCE");
        await AssertCode(await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(0, "rrule", "FREQ=DAILY", "Not/AZone", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "INVALID_TIME_ZONE");
        await AssertCode(await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(0, "rrule", "FREQ=DAILY", "Asia/Shanghai", "not-a-date")),
            HttpStatusCode.UnprocessableEntity, "INVALID_RECURRENCE");

        // Create, replay, key reuse, exists, update and stale version.
        var key1 = Guid.NewGuid();
        var dailyBody = Body(0, "rrule", "FREQ=DAILY;INTERVAL=1", "Asia/Shanghai", "2026-11-10T09:00:00");
        var created = await PutRecurrence(client, token, t1, key1, dailyBody);
        Check(created, HttpStatusCode.OK);
        var createdView = await created.Content.ReadFromJsonAsync<RecurrenceView>()
            ?? throw new InvalidOperationException("Created recurrence missing.");
        if (createdView.TaskId != t1 || createdView.Kind != "rrule" || createdView.Version != 1)
            throw new InvalidOperationException("Created recurrence projection mismatch.");
        var replay = await PutRecurrence(client, token, t1, key1, dailyBody);
        Check(replay, HttpStatusCode.OK);
        if (await replay.Content.ReadFromJsonAsync<RecurrenceView>() != createdView)
            throw new InvalidOperationException("Recurrence replay changed the response.");
        await AssertCode(await PutRecurrence(client, token, t1, key1,
            Body(0, "rrule", "FREQ=WEEKLY", "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await AssertCode(await PutRecurrence(client, token, t1, Guid.NewGuid(), dailyBody),
            HttpStatusCode.Conflict, "RECURRENCE_EXISTS");
        var updated = await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(1, "rrule", "FREQ=WEEKLY;BYDAY=MO,WE", "Asia/Shanghai", "2026-11-10T09:00:00"));
        Check(updated, HttpStatusCode.OK);
        if ((await updated.Content.ReadFromJsonAsync<RecurrenceView>())?.Version != 2)
            throw new InvalidOperationException("Updated recurrence version mismatch.");
        var stale = await PutRecurrence(client, token, t1, Guid.NewGuid(),
            Body(1, "rrule", "FREQ=DAILY", "Asia/Shanghai", "2026-11-10T09:00:00"));
        await AssertCode(stale, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(stale) is not { } details ||
            details.GetProperty("server_version").GetInt64() != 2)
            throw new InvalidOperationException("Recurrence conflict details mismatch.");
        var fetched = await (await GetRecurrence(client, token, t1)).Content.ReadFromJsonAsync<RecurrenceView>()
            ?? throw new InvalidOperationException("Fetched recurrence missing.");
        if (fetched.Rrule != "FREQ=WEEKLY;BYDAY=MO,WE" || fetched.Version != 2)
            throw new InvalidOperationException("Recurrence update not persisted.");

        // Weekly window materialization is idempotent.
        var weekly = await Occurrences(client, token, t1, "2026-11-09", "2026-11-22");
        if (weekly.Count != 3 ||
            !weekly.Select(x => x.OriginalLocalStart.Date).SequenceEqual(
                new[] { new DateTime(2026, 11, 11), new DateTime(2026, 11, 16), new DateTime(2026, 11, 18) }))
            throw new InvalidOperationException("Weekly occurrence window mismatch.");
        if (weekly[0].DueAt != new DateTimeOffset(2026, 11, 11, 1, 0, 0, TimeSpan.Zero) || weekly[0].Status != "todo")
            throw new InvalidOperationException("Weekly occurrence due_at mismatch.");
        var weeklyAgain = await Occurrences(client, token, t1, "2026-11-09", "2026-11-22");
        if (!weeklyAgain.Select(x => x.Id).SequenceEqual(weekly.Select(x => x.Id)))
            throw new InvalidOperationException("Occurrence materialization duplicated rows.");

        // DST: nonexistent spring-forward local time is skipped; fall-back ambiguity yields one earliest instant.
        Check(await PutRecurrence(client, token, t2, Guid.NewGuid(),
            Body(0, "rrule", "FREQ=DAILY", "America/New_York", "2027-03-13T02:30:00")), HttpStatusCode.OK);
        var spring = await Occurrences(client, token, t2, "2027-03-12", "2027-03-16");
        if (spring.Count != 3 ||
            !spring.Select(x => x.OriginalLocalStart.Date).SequenceEqual(
                new[] { new DateTime(2027, 3, 13), new DateTime(2027, 3, 15), new DateTime(2027, 3, 16) }))
            throw new InvalidOperationException("DST spring-forward skip mismatch.");
        if (spring[0].DueAt != new DateTimeOffset(2027, 3, 13, 7, 30, 0, TimeSpan.Zero) ||
            spring[1].DueAt != new DateTimeOffset(2027, 3, 15, 6, 30, 0, TimeSpan.Zero))
            throw new InvalidOperationException("DST spring-forward instants mismatch.");
        Check(await PutRecurrence(client, token, t3, Guid.NewGuid(),
            Body(0, "rrule", "FREQ=DAILY", "America/New_York", "2027-11-07T01:30:00")), HttpStatusCode.OK);
        var fall = await Occurrences(client, token, t3, "2027-11-06", "2027-11-08");
        if (fall.Count != 3)
            throw new InvalidOperationException("DST fall-back occurrence count mismatch.");
        if (fall.Single(x => x.OriginalLocalStart.Date == new DateTime(2027, 11, 7)).DueAt !=
            new DateTimeOffset(2027, 11, 7, 5, 30, 0, TimeSpan.Zero))
            throw new InvalidOperationException("DST fall-back earliest instant mismatch.");

        // Window validation and owner isolation.
        await AssertCode(await GetOccurrencesRaw(client, token, t1, "2026-11-22", "2026-11-09"),
            HttpStatusCode.BadRequest, "INVALID_WINDOW");
        await AssertCode(await GetOccurrencesRaw(client, token, t1, "2026-01-01", "2026-06-01"),
            HttpStatusCode.BadRequest, "INVALID_WINDOW");
        await AssertCode(await GetOccurrencesRaw(client, token, t1, "bad", "2026-11-09"),
            HttpStatusCode.BadRequest, "INVALID_WINDOW");
        await AssertCode(await GetRecurrence(client, token, tF), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await GetOccurrencesRaw(client, token, tF, "2026-11-09", "2026-11-22"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Delete rules with version guards; history stays.
        var deleteKey = Guid.NewGuid();
        Check(await DeleteRecurrence(client, token, t2, deleteKey, "\"v1\""), HttpStatusCode.NoContent);
        Check(await DeleteRecurrence(client, token, t2, deleteKey, "\"v1\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteRecurrence(client, token, t2, Guid.NewGuid(), "\"v1\""),
            HttpStatusCode.NotFound, "RECURRENCE_NOT_FOUND");
        await AssertCode(await DeleteRecurrence(client, token, t1, Guid.NewGuid(), "\"v9\""),
            HttpStatusCode.Conflict, "VERSION_CONFLICT");
        Check(await DeleteRecurrence(client, token, t1, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NoContent);
        await AssertCode(await GetOccurrencesRaw(client, token, t1, "2026-11-09", "2026-11-22"),
            HttpStatusCode.NotFound, "RECURRENCE_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await PutRecurrence(client, foreignToken, tF, Guid.NewGuid(), dailyBody),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 6 owner recurrence writes; occurrence materialization is derived state.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='recurrence'", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM recurrences WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM task_occurrences WHERE owner_id=$1", owner) != 9)
            throw new InvalidOperationException("Recurrence writes did not keep revision, changes and mutation records atomic.");
        Console.WriteLine("PASS: recurrence CRUD, RRULE window materialization, DST skip/ambiguity, owner isolation");
    }

    private static string Body(long expectedVersion, string kind, string rrule, string timeZone, string dtstart) =>
        "{\"expected_version\":" + expectedVersion + ",\"kind\":\"" + kind + "\",\"rrule\":\"" + rrule +
        "\",\"time_zone\":\"" + timeZone + "\",\"dtstart_local\":\"" + dtstart + "\"}";

    private static async Task<List<OccurrenceView>> Occurrences(HttpClient client, string? token, Guid taskId,
        string from, string to)
    {
        var response = await GetOccurrencesRaw(client, token, taskId, from, to);
        Check(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OccurrenceListView>()
            ?? throw new InvalidOperationException("Occurrence list missing.")).Items.ToList();
    }

    private static async Task<HttpResponseMessage> GetOccurrencesRaw(HttpClient client, string? token, Guid taskId,
        string from, string to) =>
        await Request(client, token, HttpMethod.Get,
            "/tasks/" + taskId + "/occurrences?from=" + from + "&to=" + to, null, null);

    private static Task<HttpResponseMessage> GetRecurrence(HttpClient client, string? token, Guid taskId) =>
        Request(client, token, HttpMethod.Get, "/tasks/" + taskId + "/recurrence", null, null);

    private static Task<HttpResponseMessage> PutRecurrence(HttpClient client, string? token, Guid taskId,
        Guid? key, string json) =>
        Request(client, token, HttpMethod.Put, "/tasks/" + taskId + "/recurrence", key, json);

    private static Task<HttpResponseMessage> DeleteRecurrence(HttpClient client, string? token, Guid taskId,
        Guid? key, string etag) =>
        Request(client, token, HttpMethod.Delete, "/tasks/" + taskId + "/recurrence", key, null, etag);

    private static async Task<HttpResponseMessage> Request(HttpClient client, string? token, HttpMethod method,
        string path, Guid? key, string? json, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task AddSession(NpgsqlConnection db, Guid owner, Guid id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            id, owner, hash);
    }

    private static async Task<JsonElement?> Details(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").TryGetProperty("details", out var value) ? value.Clone() : null;
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

    private sealed class RecurrenceFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
