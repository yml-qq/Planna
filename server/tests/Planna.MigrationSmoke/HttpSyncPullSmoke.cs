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
using Planna.Application.Sync;

internal static class HttpSyncPullSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var l1 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-sync-one@example.test'),($2,'http-sync-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id,revision) VALUES($1,3),($2,1)", owner, foreign);
        await Exec(db, """
            INSERT INTO sync_changes(owner_id,revision,change_index,entity_type,entity_id,op,entity_version,payload)
            VALUES($1,1,0,'task',$2,'upsert',1,'{"title":"A"}'),
                  ($1,2,0,'list',$3,'upsert',1,'{"name":"L"}'),
                  ($1,2,1,'task',$4,'upsert',1,'{"title":"B"}'),
                  ($1,3,0,'task',$2,'delete',2,'{"version":2}')
            """, owner, t1, l1, t2);
        await Exec(db, "INSERT INTO sync_changes(owner_id,revision,change_index,entity_type,entity_id,op,entity_version,payload) VALUES($1,1,0,'task',$2,'upsert',1,'{\"title\":\"F\"}')",
            foreign, t3);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new SyncFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Pull(client, null, null, null), HttpStatusCode.Unauthorized);

        // Full pull: ordered by committed revision and change_index.
        var full = await PullPage(client, token, null, null);
        if (full.Changes.Count != 4 || full.HasMore ||
            !full.Changes.Select(x => (x.Revision, x.ChangeIndex)).SequenceEqual(
                new[] { (1L, 0), (2L, 0), (2L, 1), (3L, 0) }) ||
            full.Changes[0].EntityType != "task" || full.Changes[1].EntityType != "list" ||
            full.Changes[1].Payload!.Value.GetProperty("name").GetString() != "L" ||
            full.Changes[2].EntityId != t2 || full.Changes[3].Op != "delete" ||
            full.Changes[3].Version != 2)
            throw new InvalidOperationException("Initial pull projection or ordering failed.");
        if (full.ServerTime == default) throw new InvalidOperationException("Pull server_time missing.");

        // Paging across a multi-change revision must not duplicate or drop changes.
        var page1 = await PullPage(client, token, "2", null);
        if (page1.Changes.Count != 2 || !page1.HasMore ||
            !page1.Changes.Select(x => (x.Revision, x.ChangeIndex)).SequenceEqual(
                new[] { (1L, 0), (2L, 0) }))
            throw new InvalidOperationException("First pull page mismatch.");
        var page2 = await PullPage(client, token, "2", page1.NextCursor);
        if (page2.Changes.Count != 2 || page2.HasMore ||
            !page2.Changes.Select(x => (x.Revision, x.ChangeIndex)).SequenceEqual(
                new[] { (2L, 1), (3L, 0) }))
            throw new InvalidOperationException("Second pull page mismatch.");
        var combined = page1.Changes.Concat(page2.Changes).ToList();
        if (combined.Select(x => (x.Revision, x.ChangeIndex)).Distinct().Count() != 4)
            throw new InvalidOperationException("Pull pages duplicated changes.");

        // Re-pull at the end returns an empty page with the same cursor.
        var end = await PullPage(client, token, null, page2.NextCursor);
        if (end.Changes.Count != 0 || end.HasMore || end.NextCursor != page2.NextCursor)
            throw new InvalidOperationException("End-of-changelog pull mismatch.");

        // Owner isolation and parameter errors.
        var foreignPage = await PullPage(client, foreignToken, null, null);
        if (foreignPage.Changes.Count != 1 || foreignPage.Changes[0].EntityId != t3)
            throw new InvalidOperationException("Pull leaked changes across owners.");
        foreach (var limit in new[] { "0", "201", "abc" })
            await AssertCode(await Pull(client, token, limit, null), HttpStatusCode.BadRequest, "INVALID_LIMIT");
        await AssertCode(await Pull(client, token, null, "garbage"), HttpStatusCode.BadRequest, "INVALID_CURSOR");
        Check(await Pull(client, token, "200", null), HttpStatusCode.OK);

        // Expired window: cursor revisions before min_available_revision are rejected.
        await Exec(db, "UPDATE owner_sync_clocks SET min_available_revision=2 WHERE owner_id=$1", owner);
        await AssertCode(await Pull(client, token, null, Cursor(1, 0)), HttpStatusCode.Gone, "CURSOR_EXPIRED");
        var retained = await PullPage(client, token, null, Cursor(2, 0));
        if (retained.Changes.Count != 2 || retained.HasMore ||
            !retained.Changes.Select(x => (x.Revision, x.ChangeIndex)).SequenceEqual(
                new[] { (2L, 1), (3L, 0) }))
            throw new InvalidOperationException("Retained pull window mismatch.");

        // Pull is read-only: no revision, change or mutation side effects.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", foreign) != 0)
            throw new InvalidOperationException("Pull mutated durable state.");
        Console.WriteLine("PASS: sync pull ordering, revision paging, has_more, 410 expiry, owner isolation");
    }

    private static async Task<SyncPullResponse> PullPage(HttpClient client, string? token, string? limit, string? cursor)
    {
        var response = await Pull(client, token, limit, cursor);
        Check(response, HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>()
            ?? throw new InvalidOperationException("Pull response missing.");
    }

    private static async Task<HttpResponseMessage> Pull(HttpClient client, string? token, string? limit, string? cursor)
    {
        var parts = new List<string>();
        if (limit is not null) parts.Add("limit=" + limit);
        if (cursor is not null) parts.Add("cursor=" + Uri.EscapeDataString(cursor));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/sync/pull" + (parts.Count == 0 ? "" : "?" + string.Join("&", parts)));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static string Cursor(long revision, int changeIndex) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(revision + ":" + changeIndex))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    private sealed class SyncFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
