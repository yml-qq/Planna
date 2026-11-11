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

internal static class HttpListSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var inbox = Guid.NewGuid();
        var l1 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        var l3 = Guid.NewGuid();
        var l4 = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-list-one@example.test'),($2,'http-list-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'Inbox','inbox'),($3,$2,'L1','custom'),($4,$2,'L2','custom'),($5,$2,'L3','custom'),($6,$2,'L4','custom'),($7,$8,'Foreign','custom')",
            inbox, owner, l1, l2, l3, l4, f1, foreign);
        await Exec(db, "UPDATE task_lists SET archived_at=now() WHERE id=$1", l2);
        await Exec(db, "UPDATE task_lists SET deleted_at=now() WHERE id=$1", l3);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'TA'),($4,$2,$3,'TB')", t1, owner, l1, t2);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new ListFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and identifier errors.
        Check(await GetLists(client, null), HttpStatusCode.Unauthorized);
        Check(await GetList(client, null, l1), HttpStatusCode.Unauthorized);
        Check(await CreateList(client, null, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await PatchList(client, null, l1, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await DeleteList(client, null, l1, Guid.NewGuid(), "\"v1\""), HttpStatusCode.Unauthorized);
        await AssertCode(await GetList(client, token, Guid.Empty), HttpStatusCode.BadRequest, "INVALID_LIST_ID");

        // List reads: inbox, active and archived lists; deleted and foreign lists hidden.
        var listing = await (await GetLists(client, token)).Content.ReadFromJsonAsync<TaskListsView>()
            ?? throw new InvalidOperationException("List page missing.");
        if (listing.Items.Count != 4 ||
            !listing.Items.Any(x => x.Id == inbox && x.Kind == "inbox" && x.Version == 1) ||
            !listing.Items.Any(x => x.Id == l1 && x.Kind == "custom") ||
            !listing.Items.Any(x => x.Id == l2) || !listing.Items.Any(x => x.Id == l4) ||
            listing.Items.Any(x => x.Id == l3 || x.Id == f1))
            throw new InvalidOperationException("List read projection or owner isolation failed.");
        var one = await (await GetList(client, token, l1)).Content.ReadFromJsonAsync<TaskListView>()
            ?? throw new InvalidOperationException("Single list missing.");
        if (one.Id != l1 || one.Name != "L1" || one.Version != 1)
            throw new InvalidOperationException("Single list projection mismatch.");
        await AssertCode(await GetList(client, token, f1), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        await AssertCode(await GetList(client, token, l3), HttpStatusCode.NotFound, "LIST_NOT_FOUND");

        // Create, replay, key reuse and id conflict.
        await AssertCode(await CreateList(client, token, null, "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"X\"}"),
            HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await CreateList(client, token, Guid.NewGuid(), "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"   \"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_LIST");
        await AssertCode(await CreateList(client, token, Guid.NewGuid(), "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"" + new string('x', 121) + "\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_LIST");
        var n1 = Guid.NewGuid();
        var createKey = Guid.NewGuid();
        var createBody = "{\"id\":\"" + n1 + "\",\"name\":\"  Work  \"}";
        var created = await CreateList(client, token, createKey, createBody);
        Check(created, HttpStatusCode.Created);
        if (created.Headers.Location?.ToString() != "/lists/" + n1)
            throw new InvalidOperationException("Created list location differs from contract.");
        var createdView = await created.Content.ReadFromJsonAsync<TaskListView>()
            ?? throw new InvalidOperationException("Created list missing.");
        if (createdView.Id != n1 || createdView.Name != "Work" || createdView.Kind != "custom" || createdView.Version != 1)
            throw new InvalidOperationException("Created list projection mismatch.");
        var replay = await CreateList(client, token, createKey, createBody);
        Check(replay, HttpStatusCode.Created);
        if (await replay.Content.ReadFromJsonAsync<TaskListView>() != createdView)
            throw new InvalidOperationException("Create replay changed the response.");
        await AssertCode(await CreateList(client, token, createKey,
            "{\"id\":\"" + n1 + "\",\"name\":\"Other\"}"), HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await AssertCode(await CreateList(client, token, Guid.NewGuid(), createBody), HttpStatusCode.Conflict, "LIST_ID_CONFLICT");
        if ((await (await GetLists(client, token)).Content.ReadFromJsonAsync<TaskListsView>())?.Items.Count != 5)
            throw new InvalidOperationException("Created list missing from listing.");

        // Rename, replay, stale version with field differences, validation and isolation.
        await AssertCode(await PatchList(client, token, l4, null, "{\"expected_version\":1,\"patch\":{\"name\":\"X\"}}"),
            HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        var patchKey = Guid.NewGuid();
        var patchBody = "{\"expected_version\":1,\"patch\":{\"name\":\"Renamed\"}}";
        var patched = await PatchList(client, token, l4, patchKey, patchBody);
        Check(patched, HttpStatusCode.OK);
        var patchedView = await patched.Content.ReadFromJsonAsync<TaskListView>()
            ?? throw new InvalidOperationException("Renamed list missing.");
        if (patchedView.Id != l4 || patchedView.Name != "Renamed" || patchedView.Version != 2 || patchedView.Kind != "custom")
            throw new InvalidOperationException("Renamed list projection mismatch.");
        Check(await PatchList(client, token, l4, patchKey, patchBody), HttpStatusCode.OK);
        var conflict = await PatchList(client, token, l4, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"name\":\"Stale\"}}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(conflict) is not { } d ||
            d.GetProperty("server_version").GetInt64() != 2 ||
            d.GetProperty("entity_id").GetGuid() != l4 ||
            !d.GetProperty("changed_fields").EnumerateArray().Any(x => x.GetString() == "name"))
            throw new InvalidOperationException("List conflict details mismatch.");
        await AssertCode(await PatchList(client, token, l4, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"name\":\"   \"}}"), HttpStatusCode.UnprocessableEntity, "INVALID_LIST");
        await AssertCode(await PatchList(client, token, f1, Guid.NewGuid(), patchBody), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        await AssertCode(await PatchList(client, token, l3, Guid.NewGuid(), patchBody), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        Check(await PatchList(client, token, l2, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"name\":\"Archived renamed\"}}"), HttpStatusCode.OK);

        // Delete guards: not empty, inbox protection, stale version, soft delete, replay.
        await AssertCode(await DeleteList(client, token, l4, null, "\"v2\""), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await DeleteList(client, token, l4, Guid.NewGuid(), ""), HttpStatusCode.BadRequest, "INVALID_VERSION");
        await AssertCode(await DeleteList(client, token, l4, Guid.NewGuid(), "\"2\""), HttpStatusCode.BadRequest, "INVALID_VERSION");
        await AssertCode(await DeleteList(client, token, l1, Guid.NewGuid(), "\"v1\""), HttpStatusCode.Conflict, "LIST_NOT_EMPTY");
        await AssertCode(await DeleteList(client, token, inbox, Guid.NewGuid(), "\"v1\""), HttpStatusCode.UnprocessableEntity, "LIST_INBOX_PROTECTED");
        await AssertCode(await DeleteList(client, token, l4, Guid.NewGuid(), "\"v9\""), HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var deleteKey = Guid.NewGuid();
        Check(await DeleteList(client, token, l4, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        Check(await DeleteList(client, token, l4, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteList(client, token, l4, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        Check(await DeleteList(client, token, l2, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteList(client, token, l3, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        await AssertCode(await DeleteList(client, token, f1, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "LIST_NOT_FOUND");

        // Foreign owner edits their own list; deleted account is rejected.
        Check(await PatchList(client, foreignToken, f1, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"name\":\"Foreign renamed\"}}"), HttpStatusCode.OK);
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await CreateList(client, foreignToken, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"X\"}"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 5 owner list writes, 1 foreign list write.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='list'", owner) != 5 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE owner_id=$1 AND list_id=$2 AND deleted_at IS NULL", owner, l1) != 2 ||
            await Scalar(db, "SELECT count(*) FROM task_lists WHERE id=$1 AND deleted_at IS NOT NULL AND version=3", l4) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_lists WHERE id=$1 AND deleted_at IS NOT NULL AND version=3", l2) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_lists WHERE id=$1 AND kind='inbox' AND deleted_at IS NULL AND version=1", inbox) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_id=$2 AND op='delete' AND entity_version=3", owner, l2) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='list'", foreign) != 1)
            throw new InvalidOperationException("List writes did not keep revision, sync changes and mutation records atomic.");
        var final = await (await GetLists(client, token)).Content.ReadFromJsonAsync<TaskListsView>()
            ?? throw new InvalidOperationException("Final listing missing.");
        if (final.Items.Count != 3 || final.Items.Any(x => x.Id != inbox && x.Id != l1 && x.Id != n1))
            throw new InvalidOperationException("Deleted lists leaked into final listing.");
        Console.WriteLine("PASS: list CRUD, inbox/archived/empty guards, version conflict details, replay, owner isolation");
    }

    private static async Task<HttpResponseMessage> GetLists(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/lists");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetList(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/lists/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CreateList(HttpClient client, string? token, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/lists")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PatchList(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/lists/" + id)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteList(HttpClient client, string? token, Guid id, Guid? key, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/lists/" + id);
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

    private sealed class ListFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
