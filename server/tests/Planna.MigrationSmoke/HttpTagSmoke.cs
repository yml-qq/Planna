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

internal static class HttpTagSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var ta = Guid.NewGuid();
        var tb = Guid.NewGuid();
        var tdel = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-tag-one@example.test'),($2,'http-tag-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO tags(id,owner_id,name,color) VALUES($1,$2,'Alpha','#ff0000'),($3,$2,'Beta',NULL),($4,$2,'Deleted',NULL),($5,$6,'Foreign',NULL)",
            ta, owner, tb, tdel, f1, foreign);
        await Exec(db, "UPDATE tags SET deleted_at=now() WHERE id=$1", tdel);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new TagFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and identifier errors.
        Check(await GetTags(client, null), HttpStatusCode.Unauthorized);
        Check(await GetTag(client, null, ta), HttpStatusCode.Unauthorized);
        Check(await CreateTag(client, null, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await PatchTag(client, null, ta, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await DeleteTag(client, null, ta, Guid.NewGuid(), "\"v1\""), HttpStatusCode.Unauthorized);
        await AssertCode(await GetTag(client, token, Guid.Empty), HttpStatusCode.BadRequest, "INVALID_TAG_ID");

        // Reads: active tags only, ordered by name; deleted and foreign tags hidden.
        var listing = await (await GetTags(client, token)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Tag page missing.");
        if (listing.Items.Count != 2 ||
            listing.Items[0].Name != "Alpha" || listing.Items[0].Color != "#ff0000" ||
            listing.Items[1].Name != "Beta" || listing.Items[1].Color is not null ||
            listing.Items.Any(x => x.Id == tdel || x.Id == f1))
            throw new InvalidOperationException("Tag read projection or owner isolation failed.");
        var one = await (await GetTag(client, token, ta)).Content.ReadFromJsonAsync<TagView>()
            ?? throw new InvalidOperationException("Single tag missing.");
        if (one.Id != ta || one.Name != "Alpha" || one.Version != 1)
            throw new InvalidOperationException("Single tag projection mismatch.");
        await AssertCode(await GetTag(client, token, f1), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await GetTag(client, token, tdel), HttpStatusCode.NotFound, "TAG_NOT_FOUND");

        // Create, replay, key reuse and conflicts.
        await AssertCode(await CreateTag(client, token, null,
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"X\"}"), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await CreateTag(client, token, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"   \"}"), HttpStatusCode.UnprocessableEntity, "INVALID_TAG");
        await AssertCode(await CreateTag(client, token, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"" + new string('x', 61) + "\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG");
        await AssertCode(await CreateTag(client, token, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Color\",\"color\":\"" + new string('c', 33) + "\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TAG");
        var tc = Guid.NewGuid();
        var createKey = Guid.NewGuid();
        var createBody = "{\"id\":\"" + tc + "\",\"name\":\"  Gamma  \",\"color\":\"#00ff00\"}";
        var created = await CreateTag(client, token, createKey, createBody);
        Check(created, HttpStatusCode.Created);
        if (created.Headers.Location?.ToString() != "/tags/" + tc)
            throw new InvalidOperationException("Created tag location differs from contract.");
        var createdView = await created.Content.ReadFromJsonAsync<TagView>()
            ?? throw new InvalidOperationException("Created tag missing.");
        if (createdView.Id != tc || createdView.Name != "Gamma" || createdView.Color != "#00ff00" || createdView.Version != 1)
            throw new InvalidOperationException("Created tag projection mismatch.");
        var replay = await CreateTag(client, token, createKey, createBody);
        Check(replay, HttpStatusCode.Created);
        if (await replay.Content.ReadFromJsonAsync<TagView>() != createdView)
            throw new InvalidOperationException("Create replay changed the response.");
        await AssertCode(await CreateTag(client, token, createKey,
            "{\"id\":\"" + tc + "\",\"name\":\"Other\"}"), HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await AssertCode(await CreateTag(client, token, Guid.NewGuid(), createBody), HttpStatusCode.Conflict, "TAG_ID_CONFLICT");
        await AssertCode(await CreateTag(client, token, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"alpha\"}"), HttpStatusCode.Conflict, "TAG_NAME_CONFLICT");
        if ((await (await GetTags(client, token)).Content.ReadFromJsonAsync<TagListView>())?.Items.Count != 3)
            throw new InvalidOperationException("Created tag missing from listing.");

        // Patch: rename and clear color, replay, stale version details, validation and conflicts.
        await AssertCode(await PatchTag(client, token, ta, null,
            "{\"expected_version\":1,\"patch\":{\"name\":\"X\"}}"), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        var patchKey = Guid.NewGuid();
        var patchBody = "{\"expected_version\":1,\"patch\":{\"name\":\"Alpha renamed\",\"color\":null}}";
        var patched = await PatchTag(client, token, ta, patchKey, patchBody);
        Check(patched, HttpStatusCode.OK);
        var patchedView = await patched.Content.ReadFromJsonAsync<TagView>()
            ?? throw new InvalidOperationException("Renamed tag missing.");
        if (patchedView.Name != "Alpha renamed" || patchedView.Color is not null || patchedView.Version != 2)
            throw new InvalidOperationException("Renamed tag projection mismatch.");
        Check(await PatchTag(client, token, ta, patchKey, patchBody), HttpStatusCode.OK);
        var conflict = await PatchTag(client, token, ta, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"name\":\"Stale\"}}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(conflict) is not { } d ||
            d.GetProperty("server_version").GetInt64() != 2 ||
            d.GetProperty("entity_id").GetGuid() != ta ||
            !d.GetProperty("changed_fields").EnumerateArray().Any(x => x.GetString() == "name"))
            throw new InvalidOperationException("Tag conflict details mismatch.");
        await AssertCode(await PatchTag(client, token, ta, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{}}"), HttpStatusCode.UnprocessableEntity, "INVALID_TAG");
        await AssertCode(await PatchTag(client, token, ta, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"name\":\"   \"}}"), HttpStatusCode.UnprocessableEntity, "INVALID_TAG");
        await AssertCode(await PatchTag(client, token, tb, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"name\":\"ALPHA RENAMED\"}}"), HttpStatusCode.Conflict, "TAG_NAME_CONFLICT");
        await AssertCode(await PatchTag(client, token, f1, Guid.NewGuid(), patchBody), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await PatchTag(client, token, tdel, Guid.NewGuid(), patchBody), HttpStatusCode.NotFound, "TAG_NOT_FOUND");

        // Delete: If-Match guard, soft delete, replay, stale and isolation.
        await AssertCode(await DeleteTag(client, token, ta, null, "\"v2\""), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await DeleteTag(client, token, ta, Guid.NewGuid(), ""), HttpStatusCode.BadRequest, "INVALID_VERSION");
        await AssertCode(await DeleteTag(client, token, ta, Guid.NewGuid(), "\"2\""), HttpStatusCode.BadRequest, "INVALID_VERSION");
        var deleteKey = Guid.NewGuid();
        Check(await DeleteTag(client, token, ta, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        Check(await DeleteTag(client, token, ta, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteTag(client, token, ta, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await DeleteTag(client, token, tb, Guid.NewGuid(), "\"v9\""), HttpStatusCode.Conflict, "VERSION_CONFLICT");
        await AssertCode(await DeleteTag(client, token, tdel, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        await AssertCode(await DeleteTag(client, token, f1, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "TAG_NOT_FOUND");
        Check(await DeleteTag(client, token, tb, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NoContent);

        // Foreign owner creates their own tag; deleted account is rejected.
        Check(await CreateTag(client, foreignToken, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Foreign2\"}"), HttpStatusCode.Created);
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await CreateTag(client, foreignToken, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"X\"}"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 4 owner tag writes, 1 foreign tag write.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='tag'", owner) != 4 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM tags WHERE id=$1 AND deleted_at IS NOT NULL AND version=3", ta) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tags WHERE id=$1 AND deleted_at IS NOT NULL AND version=2", tb) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tags WHERE id=$1 AND deleted_at IS NULL AND version=1 AND name='Gamma'", tc) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_id=$2 AND op='delete' AND entity_version=3", owner, ta) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='tag'", foreign) != 1)
            throw new InvalidOperationException("Tag writes did not keep revision, sync changes and mutation records atomic.");
        var final = await (await GetTags(client, token)).Content.ReadFromJsonAsync<TagListView>()
            ?? throw new InvalidOperationException("Final tag listing missing.");
        if (final.Items.Count != 1 || final.Items[0].Id != tc)
            throw new InvalidOperationException("Deleted tags leaked into final listing.");
        Console.WriteLine("PASS: tag CRUD, case-insensitive uniqueness, version conflict details, replay, owner isolation");
    }

    private static async Task<HttpResponseMessage> GetTags(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tags");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetTag(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tags/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CreateTag(HttpClient client, string? token, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tags")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PatchTag(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/tags/" + id)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteTag(HttpClient client, string? token, Guid id, Guid? key, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/tags/" + id);
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

    private sealed class TagFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
