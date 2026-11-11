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

internal static class HttpSyncBootstrapSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var noData = Guid.NewGuid();
        var lA = Guid.NewGuid();
        var lB = Guid.NewGuid();
        var lDel = Guid.NewGuid();
        var fL = Guid.NewGuid();
        var tA = Guid.NewGuid();
        var tB = Guid.NewGuid();
        var tDel = Guid.NewGuid();
        var tF = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-bootstrap-one@example.test'),($2,'http-bootstrap-two@example.test'),($3,'http-bootstrap-three@example.test')",
            owner, foreign, noData);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id,revision) VALUES($1,6),($2,1),($3,0)", owner, foreign, noData);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$2,'B','custom'),($4,$2,'Del','custom'),($5,$6,'F','custom')",
            lA, owner, lB, lDel, fL, foreign);
        await Exec(db, "UPDATE task_lists SET deleted_at=now() WHERE id=$1", lDel);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'A'),($4,$2,$5,'B'),($6,$2,$3,'Del')",
            tA, owner, lA, tB, lB, tDel);
        await Exec(db, "UPDATE tasks SET version=2 WHERE id=$1", tB);
        await Exec(db, "UPDATE tasks SET deleted_at=now() WHERE id=$1", tDel);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'F')", tF, foreign, fL);
        await Exec(db, """
            INSERT INTO sync_changes(owner_id,revision,change_index,entity_type,entity_id,op,entity_version,payload)
            VALUES($1,1,0,'task',$2,'upsert',1,'{"title":"A"}'),
                  ($1,2,0,'list',$3,'upsert',1,'{"name":"A"}'),
                  ($1,3,0,'task',$4,'upsert',1,'{"title":"B"}'),
                  ($1,4,0,'task',$2,'upsert',2,'{"title":"A2"}'),
                  ($1,5,0,'task',$5,'delete',2,'{"version":2}'),
                  ($1,6,0,'task',$4,'upsert',2,'{"title":"B2"}')
            """, owner, tA, lA, tB, tDel);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        var noDataSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        await AddSession(db, noData, noDataSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new BootstrapFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);
        var noDataToken = issuer.Issue(noData, noDataSession);

        Check(await Bootstrap(client, null), HttpStatusCode.Unauthorized);

        var snapshot = await BootstrapPage(client, token);
        if (snapshot.NextPageToken is not null || string.IsNullOrEmpty(snapshot.SnapshotCursor) ||
            snapshot.Tasks.Count != 2 || snapshot.Lists.Count != 2 ||
            !snapshot.Tasks.Select(x => x.Id).OrderBy(x => x).SequenceEqual(new[] { tA, tB }.OrderBy(x => x)) ||
            !snapshot.Lists.Select(x => x.Id).OrderBy(x => x).SequenceEqual(new[] { lA, lB }.OrderBy(x => x)) ||
            snapshot.Tasks.Single(x => x.Id == tA).Title != "A" ||
            snapshot.Tasks.Single(x => x.Id == tB).Version != 2)
            throw new InvalidOperationException("Bootstrap snapshot projection, filtering or cursor failed.");

        // The snapshot cursor continues incremental pulls from the committed revision.
        var before = await PullPage(client, token, snapshot.SnapshotCursor);
        if (before.Changes.Count != 0)
            throw new InvalidOperationException("Snapshot cursor returned already-in-snapshot changes.");
        await Exec(db, "INSERT INTO sync_changes(owner_id,revision,change_index,entity_type,entity_id,op,entity_version,payload) VALUES($1,7,0,'task',$2,'upsert',3,'{\"title\":\"B7\"}')",
            owner, tB);
        await Exec(db, "UPDATE owner_sync_clocks SET revision=7 WHERE owner_id=$1", owner);
        var after = await PullPage(client, token, snapshot.SnapshotCursor);
        if (after.Changes.Count != 1 || after.Changes[0].Revision != 7 || after.Changes[0].Version != 3)
            throw new InvalidOperationException("Snapshot cursor did not pick up post-snapshot changes.");

        // Owner isolation and empty owners.
        var foreignSnapshot = await BootstrapPage(client, foreignToken);
        if (foreignSnapshot.Tasks.Count != 1 || foreignSnapshot.Tasks[0].Id != tF ||
            foreignSnapshot.Lists.Count != 1 || foreignSnapshot.Lists[0].Id != fL)
            throw new InvalidOperationException("Bootstrap leaked data across owners.");
        var empty = await BootstrapPage(client, noDataToken);
        if (empty.Tasks.Count != 0 || empty.Lists.Count != 0 || string.IsNullOrEmpty(empty.SnapshotCursor))
            throw new InvalidOperationException("Empty-owner bootstrap mismatch.");

        // Bootstrap and pull are read-only.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 7 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 7 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 0)
            throw new InvalidOperationException("Bootstrap or pull mutated durable state.");
        Console.WriteLine("PASS: bootstrap snapshot, committed-revision cursor, pull handoff, owner isolation");
    }

    private static async Task<SyncBootstrapResponse> BootstrapPage(HttpClient client, string? token)
    {
        var response = await Bootstrap(client, token);
        Check(response, HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<SyncBootstrapResponse>()
            ?? throw new InvalidOperationException("Bootstrap response missing.");
    }

    private static async Task<HttpResponseMessage> Bootstrap(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sync/bootstrap");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<SyncPullResponse> PullPage(HttpClient client, string? token, string cursor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/sync/pull?cursor=" + Uri.EscapeDataString(cursor));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Check(response, HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>()
            ?? throw new InvalidOperationException("Pull response missing.");
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

    private sealed class BootstrapFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
