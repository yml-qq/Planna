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

internal static class HttpTaskWriteSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-write-one@example.test'),($2,'http-write-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'Foreign','custom')",
            listA, owner, foreignList, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var t4 = Guid.NewGuid();
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version,due_at) VALUES($1,$2,$3,'T1',5,now()+interval '1 day')", t1, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version,deleted_at) VALUES($1,$2,$3,'T2',2,now())", t2, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,all_day,due_local_date) VALUES($1,$2,$3,'T3',true,'2026-11-12')", t3, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Foreign')", t4, foreign, foreignList);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new WriteFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and identifier errors.
        Check(await Get(client, null, t1), HttpStatusCode.Unauthorized);
        Check(await Patch(client, null, t1, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await Delete(client, null, t1, Guid.NewGuid(), "\"v5\""), HttpStatusCode.Unauthorized);
        await AssertCode(await Get(client, token, Guid.Empty), HttpStatusCode.BadRequest, "INVALID_TASK_ID");
        Check(await Get(client, token, t1), HttpStatusCode.OK);
        var initial = await (await Get(client, token, t1)).Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Initial task missing.");
        if (initial.Version != 5 || initial.Title != "T1" || initial.DueAt is null || initial.CompletedAt is not null)
            throw new InvalidOperationException("Initial task projection mismatch.");

        Check(await Get(client, foreignToken, t1), HttpStatusCode.NotFound);
        Check(await Get(client, token, t2), HttpStatusCode.NotFound);
        await AssertCode(await Patch(client, token, t1, null,
            "{\"expected_version\":5,\"patch\":{\"title\":\"X\"}}"),
            HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        foreach (var etag in new[] { "", "\"5\"", "\"v0\"", "\"vx\"" })
            await AssertCode(await Delete(client, token, t1, Guid.NewGuid(), etag), HttpStatusCode.BadRequest, "INVALID_VERSION");

        // Happy update, replay and idempotency mismatch.
        var key1 = Guid.NewGuid();
        var happyBody = "{\"expected_version\":5,\"patch\":{\"title\":\"Renamed\",\"priority\":2}}";
        var updated = await Patch(client, token, t1, key1, happyBody);
        Check(updated, HttpStatusCode.OK);
        var updatedView = await updated.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Updated task missing.");
        if (updatedView.Version != 6 || updatedView.Title != "Renamed" || updatedView.Priority != 2 ||
            updatedView.DueAt is null || updatedView.Description != "" || updatedView.Status != "todo")
            throw new InvalidOperationException("Updated task projection mismatch.");
        var replay = await Patch(client, token, t1, key1, happyBody);
        Check(replay, HttpStatusCode.OK);
        if (await replay.Content.ReadFromJsonAsync<TaskReadView>() != updatedView)
            throw new InvalidOperationException("Patch replay changed the response.");
        await AssertCode(await Patch(client, token, t1, key1,
            "{\"expected_version\":5,\"patch\":{\"title\":\"Other\"}}"),
            HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");

        // Stale version reports server version and field differences.
        var conflict = await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":5,\"patch\":{\"title\":\"Stale\",\"priority\":0}}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var details = await Details(conflict);
        if (details is not { } d || d.GetProperty("entity_id").GetGuid() != t1 ||
            d.GetProperty("server_version").GetInt64() != 6 ||
            !d.GetProperty("changed_fields").EnumerateArray().Any(x => x.GetString() == "title"))
            throw new InvalidOperationException("Version conflict details mismatch.");

        // Null clears nullable fields; all_day/due_kind and field validation.
        Check(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":6,\"patch\":{\"due_at\":null}}"), HttpStatusCode.OK);
        var afterClear = await (await Get(client, token, t1)).Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Cleared task missing.");
        if (afterClear.Version != 7 || afterClear.DueAt is not null)
            throw new InvalidOperationException("Null patch did not clear due_at.");
        Check(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":7,\"patch\":{\"due_at\":\"2026-11-20T10:00:00Z\"}}"), HttpStatusCode.OK);
        Check(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":8,\"patch\":{\"description\":null}}"), HttpStatusCode.OK);
        foreach (var invalid in new[]
        {
            "{\"expected_version\":9,\"patch\":{\"title\":\"   \"}}",
            "{\"expected_version\":9,\"patch\":{\"priority\":4}}",
            "{\"expected_version\":9,\"patch\":{\"estimate_minutes\":0}}"
        })
            await AssertCode(await Patch(client, token, t1, Guid.NewGuid(), invalid),
                HttpStatusCode.UnprocessableEntity, "INVALID_TASK");
        await AssertCode(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":9,\"patch\":{\"time_zone\":\"Not/AZone\"}}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TIME_ZONE");
        await AssertCode(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":9,\"patch\":{}}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TASK");
        await AssertCode(await Patch(client, token, t3, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"due_at\":\"2026-11-20T10:00:00Z\"}}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TASK");
        Check(await Patch(client, token, t3, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"due_local_date\":null}}"), HttpStatusCode.OK);
        Check(await Patch(client, token, t3, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"due_local_date\":\"2026-11-13\"}}"), HttpStatusCode.OK);
        var t3view = await (await Get(client, token, t3)).Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("T3 missing.");
        if (t3view.Version != 3 || t3view.DueLocalDate != new DateOnly(2026, 11, 13) || !t3view.AllDay)
            throw new InvalidOperationException("All-day patch mismatch.");

        // Unknown fields and foreign/deleted targets.
        Check(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":9,\"patch\":{\"status\":\"done\"}}"), HttpStatusCode.BadRequest);
        Check(await Patch(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":9,\"extra\":1,\"patch\":{\"title\":\"X\"}}"), HttpStatusCode.BadRequest);
        await AssertCode(await Patch(client, token, t4, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"title\":\"X\"}}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Patch(client, token, t2, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"title\":\"X\"}}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Delete(client, token, t4, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Delete(client, token, t2, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Concurrent updates serialize: one wins, one reports conflict.
        var race = await Task.WhenAll(
            Patch(client, token, t3, Guid.NewGuid(), "{\"expected_version\":3,\"patch\":{\"priority\":3}}"),
            Patch(client, token, t3, Guid.NewGuid(), "{\"expected_version\":3,\"patch\":{\"priority\":1}}"));
        if (race.Count(x => x.StatusCode == HttpStatusCode.OK) != 1 ||
            race.Count(x => x.StatusCode == HttpStatusCode.Conflict) != 1)
            throw new InvalidOperationException("Concurrent patch was not serialized.");

        // Soft delete with If-Match, stale version, replay and post-delete 404.
        await AssertCode(await Delete(client, token, t1, Guid.NewGuid(), "\"v8\""),
            HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var deleteKey = Guid.NewGuid();
        Check(await Delete(client, token, t1, deleteKey, "\"v9\""), HttpStatusCode.NoContent);
        Check(await Delete(client, token, t1, deleteKey, "\"v9\""), HttpStatusCode.NoContent);
        await AssertCode(await Delete(client, token, t1, Guid.NewGuid(), "\"v9\""),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Get(client, token, t1), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        var tombstone = await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_id=$2 AND op='delete' AND entity_version=10", owner, t1);
        if (tombstone != 1 || await Scalar(db, "SELECT version FROM tasks WHERE id=$1", t1) != 10 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND deleted_at IS NOT NULL", t1) != 1)
            throw new InvalidOperationException("Soft delete did not record tombstone and version.");

        // Owners can edit their own tasks; a deleted account is rejected.
        Check(await Patch(client, foreignToken, t4, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"title\":\"Foreign edited\"}}"), HttpStatusCode.OK);
        var foreignView = await (await Get(client, foreignToken, t4)).Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Foreign task missing.");
        if (foreignView.Version != 2 || foreignView.Title != "Foreign edited")
            throw new InvalidOperationException("Foreign owner edit mismatch.");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Patch(client, foreignToken, t4, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"title\":\"Z\"}}"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Owner write accounting: 8 successful writes, all committed atomically.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 8 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 8 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 8 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 8 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", foreign) != 1)
            throw new InvalidOperationException("Task update/delete did not keep revision, sync changes and mutation records atomic.");
        Console.WriteLine("PASS: task detail read, versioned patch/replay/null-clear, conflict details, soft delete tombstone, concurrency, owner isolation");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tasks/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Patch(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/tasks/" + id)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string? token, Guid id, Guid? key, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/tasks/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-Match", etag);
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

    private sealed class WriteFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
