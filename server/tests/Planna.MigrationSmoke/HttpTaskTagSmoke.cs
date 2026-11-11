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
using Planna.Application.Tags;
using Planna.Application.Tasks;

internal static class HttpTaskTagSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var tA = Guid.NewGuid();
        var tB = Guid.NewGuid();
        var tC = Guid.NewGuid();
        var tD = Guid.NewGuid();
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var gdel = Guid.NewGuid();
        var gf = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-tasktag-one@example.test'),($2,'http-tasktag-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'Foreign','custom')",
            listA, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version) VALUES($1,$2,$3,'TA',2),($4,$2,$3,'TB',1),($5,$2,$3,'TD',1)",
            tA, owner, listA, tB, tD);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'TC')", tC, foreign, foreignList);
        await Exec(db, "UPDATE tasks SET deleted_at=now() WHERE id=$1", tD);
        await Exec(db, "INSERT INTO tags(id,owner_id,name) VALUES($1,$2,'G1'),($3,$2,'G2'),($4,$2,'GDel')", g1, owner, g2, gdel);
        await Exec(db, "INSERT INTO tags(id,owner_id,name) VALUES($1,$2,'GForeign')", gf, foreign);
        await Exec(db, "UPDATE tags SET deleted_at=now() WHERE id=$1", gdel);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new TaskTagFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication.
        Check(await GetTaskTags(client, null, tA), HttpStatusCode.Unauthorized);
        Check(await PutTaskTags(client, null, tA, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await GetTasks(client, null, "tag_id=" + g1), HttpStatusCode.Unauthorized);

        // Empty set, foreign and deleted tasks.
        var empty = await (await GetTaskTags(client, token, tA)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Task tag page missing.");
        if (empty.Items.Count != 0) throw new InvalidOperationException("New task already had tags.");
        await AssertCode(await GetTaskTags(client, token, tC), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await GetTaskTags(client, token, tD), HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Replace, replay, key reuse and stale version details.
        var key1 = Guid.NewGuid();
        var body1 = "{\"expected_version\":2,\"tag_ids\":[\"" + g1 + "\",\"" + g2 + "\"]}";
        var set = await PutTaskTags(client, token, tA, key1, body1);
        Check(set, HttpStatusCode.OK);
        var setView = await set.Content.ReadFromJsonAsync<TaskTagSetView>()
            ?? throw new InvalidOperationException("Tag set missing.");
        if (setView.TaskId != tA || setView.Version != 3 ||
            !setView.TagIds.SequenceEqual(new[] { g1, g2 }))
            throw new InvalidOperationException("Tag set projection mismatch.");
        var replay = await PutTaskTags(client, token, tA, key1, body1);
        Check(replay, HttpStatusCode.OK);
        var replayView = await replay.Content.ReadFromJsonAsync<TaskTagSetView>()
            ?? throw new InvalidOperationException("Replayed tag set missing.");
        if (replayView.TaskId != setView.TaskId || replayView.Version != setView.Version ||
            !replayView.TagIds.SequenceEqual(setView.TagIds))
            throw new InvalidOperationException("Tag set replay changed the response.");
        await AssertCode(await PutTaskTags(client, token, tA, key1,
            "{\"expected_version\":2,\"tag_ids\":[\"" + g1 + "\"]}"), HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        var conflict = await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":2,\"tag_ids\":[\"" + g1 + "\"]}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(conflict) is not { } d ||
            d.GetProperty("server_version").GetInt64() != 3 ||
            d.GetProperty("entity_id").GetGuid() != tA ||
            !d.GetProperty("changed_fields").EnumerateArray().Any(x => x.GetString() == "tag_ids"))
            throw new InvalidOperationException("Tag set conflict details mismatch.");

        var tags = await (await GetTaskTags(client, token, tA)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Task tags missing.");
        if (!tags.Items.Select(x => x.Name).SequenceEqual(new[] { "G1", "G2" }))
            throw new InvalidOperationException("Task tag list mismatch.");
        var byG1 = await (await GetTasks(client, token, "tag_id=" + g1)).Content.ReadFromJsonAsync<TaskReadPage>()
            ?? throw new InvalidOperationException("Tag filtered page missing.");
        if (byG1.Items.Count != 1 || byG1.Items[0].Id != tA)
            throw new InvalidOperationException("tag_id filter mismatch.");

        // Replace with a subset, then clear.
        Check(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":3,\"tag_ids\":[\"" + g2 + "\"]}"), HttpStatusCode.OK);
        var subset = await (await GetTaskTags(client, token, tA)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Subset tags missing.");
        if (!subset.Items.Select(x => x.Id).SequenceEqual(new[] { g2 }))
            throw new InvalidOperationException("Tag subset mismatch.");
        var noG1 = await (await GetTasks(client, token, "tag_id=" + g1)).Content.ReadFromJsonAsync<TaskReadPage>()
            ?? throw new InvalidOperationException("Empty tag filtered page missing.");
        if (noG1.Items.Count != 0 || noG1.NextCursor is not null)
            throw new InvalidOperationException("tag_id filter returned tasks without the tag.");
        Check(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":4,\"tag_ids\":[]}"), HttpStatusCode.OK);
        var cleared = await (await GetTaskTags(client, token, tA)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Cleared tags missing.");
        if (cleared.Items.Count != 0) throw new InvalidOperationException("Tags were not cleared.");

        // Invalid tag sets and filters.
        await AssertCode(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":5,\"tag_ids\":[\"" + Guid.NewGuid() + "\"]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG_SET");
        await AssertCode(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":5,\"tag_ids\":[\"" + gdel + "\"]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG_SET");
        await AssertCode(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":5,\"tag_ids\":[\"" + gf + "\"]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG_SET");
        var many = string.Join(",", Enumerable.Range(0, 21).Select(_ => "\"" + Guid.NewGuid() + "\""));
        await AssertCode(await PutTaskTags(client, token, tA, Guid.NewGuid(),
            "{\"expected_version\":5,\"tag_ids\":[" + many + "]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG_SET");
        await AssertCode(await GetTasks(client, token, "tag_id=not-a-uuid"), HttpStatusCode.BadRequest, "INVALID_TAG_ID");
        await AssertCode(await GetTasks(client, token, "tag_id=" + gf), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await GetTasks(client, token, "tag_id=" + gdel), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await PutTaskTags(client, token, tC, Guid.NewGuid(),
            "{\"expected_version\":1,\"tag_ids\":[]}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await PutTaskTags(client, token, tD, Guid.NewGuid(),
            "{\"expected_version\":1,\"tag_ids\":[]}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Foreign owner attaches their own tag; deleted account is rejected.
        Check(await PutTaskTags(client, foreignToken, tC, Guid.NewGuid(),
            "{\"expected_version\":1,\"tag_ids\":[\"" + gf + "\"]}"), HttpStatusCode.OK);
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await PutTaskTags(client, foreignToken, tC, Guid.NewGuid(),
            "{\"expected_version\":2,\"tag_ids\":[]}"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 3 owner tag-set writes, 1 foreign.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='task'", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND payload ? 'tag_ids'", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM task_tags WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=5", tA) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_tags WHERE owner_id=$1 AND task_id=$2", foreign, tC) != 1)
            throw new InvalidOperationException("Task tag writes did not keep revision, associations and mutation records atomic.");
        Console.WriteLine("PASS: task tag replacement, tag_id filter, version conflict details, replay, owner isolation");
    }

    private static async Task<HttpResponseMessage> GetTasks(HttpClient client, string? token, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tasks?" + query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetTaskTags(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tasks/" + id + "/tags");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutTaskTags(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/tasks/" + id + "/tags")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
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

    private sealed class TaskTagFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
