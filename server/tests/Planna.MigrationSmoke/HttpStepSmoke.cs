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

internal static class HttpStepSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var tF = Guid.NewGuid();
        var tDel = Guid.NewGuid();
        var sA = Guid.NewGuid();
        var sB = Guid.NewGuid();
        var sDel = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-step-one@example.test'),($2,'http-step-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'Foreign','custom')",
            listA, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'T1'),($4,$2,$3,'TDel')", t1, owner, listA, tDel);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'TF')", tF, foreign, foreignList);
        await Exec(db, "UPDATE tasks SET deleted_at=now() WHERE id=$1", tDel);
        await Exec(db, "INSERT INTO task_steps(id,owner_id,task_id,title,position) VALUES($1,$2,$3,'Step A',10),($4,$2,$3,'Step B',20),($5,$2,$3,'Deleted',30)",
            sA, owner, t1, sB, sDel);
        await Exec(db, "UPDATE task_steps SET deleted_at=now() WHERE id=$1", sDel);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new StepFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and identifier errors.
        Check(await GetSteps(client, null, t1), HttpStatusCode.Unauthorized);
        Check(await CreateStep(client, null, t1, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await PatchStep(client, null, sA, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await DeleteStep(client, null, sA, Guid.NewGuid(), "\"v1\""), HttpStatusCode.Unauthorized);
        await AssertCode(await GetSteps(client, token, Guid.Empty), HttpStatusCode.BadRequest, "INVALID_TASK_ID");
        await AssertCode(await PatchStep(client, token, Guid.Empty, Guid.NewGuid(), "{}"), HttpStatusCode.BadRequest, "INVALID_STEP_ID");

        // Step reads: ordered by position; foreign and deleted tasks hidden.
        var listing = await (await GetSteps(client, token, t1)).Content.ReadFromJsonAsync<StepListView>()
            ?? throw new InvalidOperationException("Step page missing.");
        if (!listing.Items.Select(x => x.Id).SequenceEqual(new[] { sA, sB }) ||
            listing.Items[0].Position != 10 || listing.Items[1].Position != 20)
            throw new InvalidOperationException("Step ordering or projection failed.");
        await AssertCode(await GetSteps(client, token, tF), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await GetSteps(client, token, tDel), HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Create, replay, key reuse and id conflict.
        await AssertCode(await CreateStep(client, token, t1, null,
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"X\"}"), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await CreateStep(client, token, t1, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"   \"}"), HttpStatusCode.UnprocessableEntity, "INVALID_STEP");
        await AssertCode(await CreateStep(client, token, t1, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"X\",\"estimate_minutes\":0}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_STEP");
        var sC = Guid.NewGuid();
        var createKey = Guid.NewGuid();
        var createBody = "{\"id\":\"" + sC + "\",\"title\":\"  Step C  \",\"position\":30,\"estimate_minutes\":25}";
        var created = await CreateStep(client, token, t1, createKey, createBody);
        Check(created, HttpStatusCode.Created);
        if (created.Headers.Location?.ToString() != "/steps/" + sC)
            throw new InvalidOperationException("Created step location differs from contract.");
        var createdView = await created.Content.ReadFromJsonAsync<StepView>()
            ?? throw new InvalidOperationException("Created step missing.");
        if (createdView.Id != sC || createdView.Title != "Step C" || createdView.IsDone ||
            createdView.Position != 30 || createdView.EstimateMinutes != 25 || createdView.Version != 1)
            throw new InvalidOperationException("Created step projection mismatch.");
        var replay = await CreateStep(client, token, t1, createKey, createBody);
        Check(replay, HttpStatusCode.Created);
        if (await replay.Content.ReadFromJsonAsync<StepView>() != createdView)
            throw new InvalidOperationException("Create replay changed the response.");
        await AssertCode(await CreateStep(client, token, t1, createKey,
            "{\"id\":\"" + sC + "\",\"title\":\"Other\"}"), HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await AssertCode(await CreateStep(client, token, t1, Guid.NewGuid(), createBody),
            HttpStatusCode.Conflict, "STEP_ID_CONFLICT");
        await AssertCode(await CreateStep(client, token, tF, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"X\"}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await CreateStep(client, token, tDel, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"X\"}"), HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Patch: estimate clear, rename/done, replay, stale details and validation.
        await AssertCode(await PatchStep(client, token, sA, null,
            "{\"expected_version\":1,\"patch\":{\"title\":\"X\"}}"), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        Check(await PatchStep(client, token, sC, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"estimate_minutes\":null}}"), HttpStatusCode.OK);
        var patchKey = Guid.NewGuid();
        var patchBody = "{\"expected_version\":1,\"patch\":{\"title\":\"Renamed\",\"is_done\":true}}";
        var patched = await PatchStep(client, token, sA, patchKey, patchBody);
        Check(patched, HttpStatusCode.OK);
        var patchedView = await patched.Content.ReadFromJsonAsync<StepView>()
            ?? throw new InvalidOperationException("Renamed step missing.");
        if (patchedView.Title != "Renamed" || !patchedView.IsDone || patchedView.Version != 2)
            throw new InvalidOperationException("Renamed step projection mismatch.");
        Check(await PatchStep(client, token, sA, patchKey, patchBody), HttpStatusCode.OK);
        var conflict = await PatchStep(client, token, sA, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"title\":\"Stale\"}}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(conflict) is not { } d ||
            d.GetProperty("server_version").GetInt64() != 2 ||
            d.GetProperty("entity_id").GetGuid() != sA ||
            !d.GetProperty("changed_fields").EnumerateArray().Any(x => x.GetString() == "title"))
            throw new InvalidOperationException("Step conflict details mismatch.");
        await AssertCode(await PatchStep(client, token, sA, Guid.NewGuid(),
            "{\"expected_version\":2,\"patch\":{\"title\":\"   \"}}"), HttpStatusCode.UnprocessableEntity, "INVALID_STEP");
        await AssertCode(await PatchStep(client, token, sDel, Guid.NewGuid(),
            "{\"expected_version\":1,\"patch\":{\"title\":\"X\"}}"), HttpStatusCode.NotFound, "STEP_NOT_FOUND");

        // Delete guards, soft delete and replay.
        await AssertCode(await DeleteStep(client, token, sB, null, "\"v1\""), HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        await AssertCode(await DeleteStep(client, token, sB, Guid.NewGuid(), ""), HttpStatusCode.BadRequest, "INVALID_VERSION");
        await AssertCode(await DeleteStep(client, token, sB, Guid.NewGuid(), "\"v9\""), HttpStatusCode.Conflict, "VERSION_CONFLICT");
        var deleteKey = Guid.NewGuid();
        Check(await DeleteStep(client, token, sA, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        Check(await DeleteStep(client, token, sA, deleteKey, "\"v2\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteStep(client, token, sA, Guid.NewGuid(), "\"v2\""), HttpStatusCode.NotFound, "STEP_NOT_FOUND");
        Check(await DeleteStep(client, token, sB, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NoContent);
        await AssertCode(await DeleteStep(client, token, sDel, Guid.NewGuid(), "\"v1\""), HttpStatusCode.NotFound, "STEP_NOT_FOUND");

        // Foreign owner creates a step on their own task; deleted account is rejected.
        var sF = Guid.NewGuid();
        Check(await CreateStep(client, foreignToken, tF, Guid.NewGuid(),
            "{\"id\":\"" + sF + "\",\"title\":\"Foreign step\"}"), HttpStatusCode.Created);
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await CreateStep(client, foreignToken, tF, Guid.NewGuid(),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"X\"}"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 5 owner step writes, 1 foreign.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='step'", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=1", t1) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE id=$1 AND deleted_at IS NOT NULL AND version=3", sA) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE id=$1 AND deleted_at IS NOT NULL AND version=2", sB) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE id=$1 AND deleted_at IS NULL AND version=2 AND estimate_minutes IS NULL", sC) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE owner_id=$1 AND task_id=$2 AND deleted_at IS NULL", foreign, tF) != 1)
            throw new InvalidOperationException("Step writes did not keep revision, task version and mutation records atomic.");
        var final = await (await GetSteps(client, token, t1)).Content.ReadFromJsonAsync<StepListView>()
            ?? throw new InvalidOperationException("Final step listing missing.");
        if (final.Items.Count != 1 || final.Items[0].Id != sC || final.Items[0].EstimateMinutes is not null)
            throw new InvalidOperationException("Deleted steps leaked into final listing.");
        Console.WriteLine("PASS: step CRUD, ordering, version conflict details, replay, estimate clear, owner isolation");
    }

    private static async Task<HttpResponseMessage> GetSteps(HttpClient client, string? token, Guid taskId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tasks/" + taskId + "/steps");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CreateStep(HttpClient client, string? token, Guid taskId, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + taskId + "/steps")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PatchStep(HttpClient client, string? token, Guid stepId, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/steps/" + stepId)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteStep(HttpClient client, string? token, Guid stepId, Guid? key, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/steps/" + stepId);
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

    private sealed class StepFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
