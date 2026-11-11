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

internal static class HttpSyncPushSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var listDel = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var tUpd = Guid.NewGuid();
        var tDel = Guid.NewGuid();
        var tNew = Guid.NewGuid();
        var tForeign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-push-one@example.test'),($2,'http-push-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$2,'Del','custom'),($4,$5,'Foreign','custom')",
            listA, owner, listDel, foreignList, foreign);
        await Exec(db, "UPDATE task_lists SET deleted_at=now() WHERE id=$1", listDel);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version) VALUES($1,$2,$3,'Old',3),($4,$2,$3,'ToDelete',1)",
            tUpd, owner, listA, tDel);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Foreign')", tForeign, foreign, foreignList);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new PushFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);
        var device = Guid.NewGuid();

        Check(await Push(client, null, device, "[]"), HttpStatusCode.Unauthorized);

        // One batch exercising accepted/duplicate/conflict/rejected.
        var m1 = Guid.NewGuid();
        var m3 = Guid.NewGuid();
        var m5 = Guid.NewGuid();
        var batch = "[" + string.Join(",",
            Mut(m1, "task", tNew, "upsert", 0, "{\"list_id\":\"" + listA + "\",\"title\":\"New\"}"),
            Mut(m1, "task", tNew, "upsert", 0, "{\"list_id\":\"" + listA + "\",\"title\":\"New\"}"),
            Mut(m3, "task", tUpd, "upsert", 3, "{\"title\":\"Updated\"}"),
            Mut(Guid.NewGuid(), "task", tUpd, "upsert", 3, "{\"title\":\"Stale\"}"),
            Mut(m5, "task", tDel, "delete", 1, "{}"),
            Mut(Guid.NewGuid(), "task", tDel, "delete", 1, "{}"),
            Mut(Guid.NewGuid(), "task", tUpd, "upsert", 0, "{\"list_id\":\"" + listA + "\",\"title\":\"Dup\"}"),
            Mut(Guid.NewGuid(), "task", tForeign, "upsert", 1, "{\"title\":\"Hijack\"}"),
            Mut(Guid.NewGuid(), "task", Guid.NewGuid(), "upsert", 0, "{\"list_id\":\"" + foreignList + "\",\"title\":\"X\"}"),
            Mut(Guid.NewGuid(), "task", Guid.NewGuid(), "upsert", 0, "{\"list_id\":\"" + listDel + "\",\"title\":\"X\"}"),
            Mut(Guid.NewGuid(), "task", Guid.NewGuid(), "upsert", 0, "{\"list_id\":\"" + listA + "\",\"title\":\"   \"}"),
            Mut(Guid.NewGuid(), "reminder", Guid.NewGuid(), "upsert", 0, "{}"),
            Mut(Guid.NewGuid(), "task", tUpd, "delete", 0, "{}"),
            Mut(Guid.NewGuid(), "task", tUpd, "upsert", 3, "{\"title\":\"Stale2\"}")) + "]";
        var pushed = await Push(client, token, device, batch);
        Check(pushed, HttpStatusCode.OK);
        var results = (await pushed.Content.ReadFromJsonAsync<SyncPushResponse>()
            ?? throw new InvalidOperationException("Push response missing.")).Results;
        if (results.Count != 14)
            throw new InvalidOperationException("Push result count mismatch.");
        AssertResult(results[0], "accepted", 1);
        if (results[0].ServerEntity!.Value.GetProperty("title").GetString() != "New")
            throw new InvalidOperationException("Accepted create projection mismatch.");
        AssertResult(results[1], "duplicate", 1);
        AssertResult(results[2], "accepted", 4);
        AssertResult(results[3], "conflict", 4);
        if (results[3].ServerEntity!.Value.GetProperty("title").GetString() != "Updated" || !results[3].Retryable)
            throw new InvalidOperationException("Conflict server_entity mismatch.");
        AssertResult(results[4], "accepted", 2);
        AssertResult(results[5], "duplicate", 2);
        AssertResult(results[6], "conflict", 4);
        AssertResult(results[7], "rejected", null);
        AssertResult(results[8], "rejected", null);
        AssertResult(results[9], "rejected", null);
        AssertResult(results[10], "rejected", null);
        AssertResult(results[11], "rejected", null);
        AssertResult(results[12], "rejected", null);
        AssertResult(results[13], "conflict", 4);

        // Replaying an applied mutation returns duplicate with the stored version.
        var replay = await Push(client, token, device,
            "[" + Mut(m3, "task", tUpd, "upsert", 3, "{\"title\":\"Updated\"}") + "]");
        Check(replay, HttpStatusCode.OK);
        var replayResult = (await replay.Content.ReadFromJsonAsync<SyncPushResponse>()
            ?? throw new InvalidOperationException("Replay response missing.")).Results.Single();
        AssertResult(replayResult, "duplicate", 4);

        // Invalid whole-batch requests.
        await AssertCode(await Push(client, token, Guid.Empty,
            "[" + Mut(Guid.NewGuid(), "task", Guid.NewGuid(), "delete", 1, "{}") + "]"),
            HttpStatusCode.BadRequest, "INVALID_SYNC_REQUEST");
        await AssertCode(await Push(client, token, device, "[]"), HttpStatusCode.BadRequest, "INVALID_SYNC_REQUEST");
        var many = "[" + string.Join(",", Enumerable.Range(0, 101)
            .Select(_ => Mut(Guid.NewGuid(), "task", Guid.NewGuid(), "delete", 1, "{}"))) + "]";
        await AssertCode(await Push(client, token, device, many), HttpStatusCode.BadRequest, "INVALID_SYNC_REQUEST");
        await AssertCode(await Push(client, token, device,
            "[" + Mut(Guid.Empty, "task", Guid.NewGuid(), "delete", 1, "{}") + "]"),
            HttpStatusCode.BadRequest, "INVALID_SYNC_REQUEST");

        // Foreign owner pushes their own update; owner isolation holds.
        var foreignPush = await Push(client, foreignToken, Guid.NewGuid(),
            "[" + Mut(Guid.NewGuid(), "task", tForeign, "upsert", 1, "{\"title\":\"Foreign updated\"}") + "]");
        Check(foreignPush, HttpStatusCode.OK);
        AssertResult((await foreignPush.Content.ReadFromJsonAsync<SyncPushResponse>()
            ?? throw new InvalidOperationException("Foreign push response missing.")).Results.Single(), "accepted", 2);

        // Atomic accounting: 3 accepted owner mutations, 1 foreign.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=1 AND title='New'", tNew) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=4 AND title='Updated'", tUpd) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND deleted_at IS NOT NULL AND version=2", tDel) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_id=$2 AND op='delete' AND entity_version=2", owner, tDel) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=2 AND title='Foreign updated'", tForeign) != 1)
            throw new InvalidOperationException("Sync push did not keep revision, changes and mutation records atomic.");
        Console.WriteLine("PASS: sync push batch statuses, idempotent replay, conflict entities, owner isolation");
    }

    private static string Mut(Guid mutationId, string entityType, Guid entityId, string op, long baseVersion, string payload) =>
        "{\"mutation_id\":\"" + mutationId + "\",\"entity_type\":\"" + entityType + "\",\"entity_id\":\"" +
        entityId + "\",\"op\":\"" + op + "\",\"base_version\":" + baseVersion + ",\"payload\":" + payload + "}";

    private static void AssertResult(SyncPushResult result, string status, long? version)
    {
        if (result.Status != status || result.EntityVersion != version)
            throw new InvalidOperationException(
                $"Expected {status}/{version?.ToString() ?? "null"}, got {result.Status}/{result.EntityVersion?.ToString() ?? "null"}.");
    }

    private static async Task<HttpResponseMessage> Push(HttpClient client, string? token, Guid device, string mutations)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sync/push")
        {
            Content = new StringContent("{\"device_id\":\"" + device + "\",\"mutations\":" + mutations + "}",
                Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private sealed class PushFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
