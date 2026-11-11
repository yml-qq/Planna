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
using Planna.Application.Ai;
using Planna.Application.Identity;

internal static class HttpPlanSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var list = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var ta = Guid.NewGuid();
        var tb = Guid.NewGuid();
        var tc = Guid.NewGuid();
        var tF = Guid.NewGuid();
        var tDel = Guid.NewGuid();
        var block = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-plan-one@example.test'),($2,'http-plan-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'F','custom')",
            list, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version,estimate_minutes) VALUES($1,$2,$3,'A',2,30),($4,$2,$3,'B',1,30),($5,$2,$3,'C',1,45)",
            ta, owner, list, tb, tc);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Foreign')", tF, foreign, foreignList);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,deleted_at) VALUES($1,$2,$3,'Deleted',now())", tDel, owner, list);
        await Exec(db, "INSERT INTO task_time_blocks(id,owner_id,task_id,start_at,end_at) VALUES($1,$2,$3,'2026-12-01T10:00:00Z','2026-12-01T10:20:00Z')", block, owner, ta);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("AI_PROVIDER", "rule_based");
        await using var factory = new PlanFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Propose(client, null, ProposeBody([ta], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z")),
            HttpStatusCode.Unauthorized);
        Check(await Apply(client, null, Guid.NewGuid(), Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);

        await AssertCode(await Propose(client, token, ProposeBody([], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z")),
            HttpStatusCode.UnprocessableEntity, "INVALID_PLAN");
        await AssertCode(await Propose(client, token, ProposeBody([ta], "2026-12-01T12:00:00Z", "2026-12-01T10:00:00Z")),
            HttpStatusCode.UnprocessableEntity, "INVALID_PLAN");
        await AssertCode(await Propose(client, token, ProposeBody([ta], "2026-12-01T10:00:00Z", "2026-12-10T10:00:00Z")),
            HttpStatusCode.UnprocessableEntity, "INVALID_PLAN");
        await AssertCode(await Propose(client, token, ProposeBody([tF], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z")),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Propose(client, token, ProposeBody([tDel], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z")),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Preview: overlapping task conflicts, others get sequential blocks.
        var proposed = await Propose(client, token, ProposeBody([ta, tb, tc], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z"));
        Check(proposed, HttpStatusCode.OK);
        var plan = await proposed.Content.ReadFromJsonAsync<AiPlanView>()
            ?? throw new InvalidOperationException("Plan preview missing.");
        if (plan.Operations.Count != 2 || plan.Conflicts.Count != 1 ||
            plan.UnplannedTaskIds.Count != 1 || plan.UnplannedTaskIds[0] != ta ||
            plan.Operations[0].TaskId != tb || plan.Operations[0].ExpectedVersion != 1 ||
            plan.Operations[0].After.StartAt != new DateTimeOffset(2026, 12, 1, 10, 30, 0, TimeSpan.Zero) ||
            plan.Operations[0].After.EndAt != new DateTimeOffset(2026, 12, 1, 11, 0, 0, TimeSpan.Zero) ||
            plan.Operations[1].After.StartAt != new DateTimeOffset(2026, 12, 1, 11, 0, 0, TimeSpan.Zero) ||
            plan.Operations[1].After.EndAt != new DateTimeOffset(2026, 12, 1, 11, 45, 0, TimeSpan.Zero) ||
            string.IsNullOrEmpty(plan.SnapshotHash) || string.IsNullOrEmpty(plan.ConfirmationToken) ||
            plan.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrEmpty(plan.Explanation))
            throw new InvalidOperationException("Plan preview projection or scheduling mismatch.");

        // Apply guards: wrong operation set, snapshot and token.
        var allIds = plan.Operations.Select(x => x.OperationId).ToList();
        await AssertCode(await Apply(client, token, plan.PlanId, Guid.NewGuid(),
            ApplyBody(plan.SnapshotHash, [allIds[0]], plan.ConfirmationToken)), HttpStatusCode.Conflict, "PLAN_STALE");
        await AssertCode(await Apply(client, token, plan.PlanId, Guid.NewGuid(),
            ApplyBody("wrong-snapshot-value", allIds, plan.ConfirmationToken)), HttpStatusCode.Conflict, "PLAN_STALE");
        await AssertCode(await Apply(client, token, plan.PlanId, Guid.NewGuid(),
            ApplyBody(plan.SnapshotHash, allIds, "wrong-token-value")), HttpStatusCode.Conflict, "PLAN_STALE");

        var applyKey = Guid.NewGuid();
        var applyBody = ApplyBody(plan.SnapshotHash, allIds, plan.ConfirmationToken);
        var applied = await Apply(client, token, plan.PlanId, applyKey, applyBody);
        Check(applied, HttpStatusCode.OK);
        var appliedView = await applied.Content.ReadFromJsonAsync<ApplyPlanResponse>()
            ?? throw new InvalidOperationException("Applied plan missing.");
        if (appliedView.Status != "applied" || !appliedView.AppliedOperationIds.SequenceEqual(allIds))
            throw new InvalidOperationException("Applied plan response mismatch.");
        var replay = await Apply(client, token, plan.PlanId, applyKey, applyBody);
        Check(replay, HttpStatusCode.OK);
        if ((await replay.Content.ReadFromJsonAsync<ApplyPlanResponse>())?.AppliedOperationIds.SequenceEqual(allIds) != true)
            throw new InvalidOperationException("Plan apply replay mismatch.");
        await AssertCode(await Apply(client, token, plan.PlanId, Guid.NewGuid(), applyBody),
            HttpStatusCode.Conflict, "PLAN_STALE");
        if (await Scalar(db, "SELECT count(*) FROM task_time_blocks WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM ai_plans WHERE id=$1 AND status='applied'", plan.PlanId) != 1 ||
            await Scalar(db, "SELECT count(*) FROM task_time_blocks WHERE owner_id=$1 AND task_id=$2 AND start_at='2026-12-01T10:30:00Z'", owner, tb) != 1)
            throw new InvalidOperationException("Plan apply did not create time blocks atomically.");

        // Stale task version after the preview blocks the plan.
        var stalePlan = await (await Propose(client, token, ProposeBody([tb], "2026-12-01T13:00:00Z", "2026-12-01T14:00:00Z")))
            .Content.ReadFromJsonAsync<AiPlanView>()
            ?? throw new InvalidOperationException("Stale-candidate plan missing.");
        await Exec(db, "UPDATE tasks SET version=version+1 WHERE id=$1", tb);
        await AssertCode(await Apply(client, token, stalePlan.PlanId, Guid.NewGuid(),
            ApplyBody(stalePlan.SnapshotHash, stalePlan.Operations.Select(x => x.OperationId).ToList(), stalePlan.ConfirmationToken)),
            HttpStatusCode.Conflict, "PLAN_STALE");

        // Expired plans cannot be applied.
        var expiredPlan = await (await Propose(client, token, ProposeBody([tb], "2026-12-01T13:00:00Z", "2026-12-01T14:00:00Z")))
            .Content.ReadFromJsonAsync<AiPlanView>()
            ?? throw new InvalidOperationException("Expired-candidate plan missing.");
        await Exec(db, "UPDATE ai_plans SET expires_at=now()-interval '1 minute' WHERE id=$1", expiredPlan.PlanId);
        await AssertCode(await Apply(client, token, expiredPlan.PlanId, Guid.NewGuid(),
            ApplyBody(expiredPlan.SnapshotHash, expiredPlan.Operations.Select(x => x.OperationId).ToList(), expiredPlan.ConfirmationToken)),
            HttpStatusCode.Conflict, "PLAN_STALE");

        // Owner isolation and deleted account.
        await AssertCode(await Apply(client, foreignToken, plan.PlanId, Guid.NewGuid(), applyBody),
            HttpStatusCode.NotFound, "PLAN_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Propose(client, foreignToken, ProposeBody([tF], "2026-12-01T10:00:00Z", "2026-12-01T12:00:00Z")),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: one revision for the applied block batch.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='time_block'", owner) != 2 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM ai_action_audits WHERE owner_id=$1 AND action_type='plan_apply'", owner) != 1)
            throw new InvalidOperationException("Plan apply did not keep revision, changes, mutations and audit consistent.");
        Console.WriteLine("PASS: plan preview scheduling/conflicts, apply guards, atomic time blocks, replay, owner isolation");
    }

    private static string ProposeBody(IEnumerable<Guid> taskIds, string start, string end) =>
        "{\"task_ids\":[" + string.Join(",", taskIds.Select(x => "\"" + x + "\"")) + "]," +
        "\"range_start\":\"" + start + "\",\"range_end\":\"" + end + "\"}";

    private static string ApplyBody(string snapshot, IEnumerable<Guid> ids, string tokenValue) =>
        "{\"snapshot_hash\":\"" + snapshot + "\",\"accepted_operation_ids\":[" +
        string.Join(",", ids.Select(x => "\"" + x + "\"")) + "]," +
        "\"confirmation_token\":\"" + tokenValue + "\"}";

    private static async Task<HttpResponseMessage> Propose(HttpClient client, string? token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/plans")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Apply(HttpClient client, string? token, Guid planId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/plans/" + planId + "/apply")
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

    private sealed class PlanFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
