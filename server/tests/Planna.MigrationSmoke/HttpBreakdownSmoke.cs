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

internal static class HttpBreakdownSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var list = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var tF = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-breakdown-one@example.test'),($2,'http-breakdown-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'F','custom')",
            list, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,version) VALUES($1,$2,$3,'准备文档，撰写内容，校对',2)", t1, owner, list);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Foreign')", tF, foreign, foreignList);
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
        await using var factory = new BreakdownFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Create(client, null, t1, Guid.NewGuid(), "{\"expected_version\":2}"), HttpStatusCode.Unauthorized);
        Check(await Apply(client, null, Guid.NewGuid(), Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);

        await AssertCode(await Create(client, token, t1, Guid.NewGuid(), "{\"expected_version\":0}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_BREAKDOWN");
        await AssertCode(await Create(client, token, Guid.NewGuid(), Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        var versionConflict = await Create(client, token, t1, Guid.NewGuid(), "{\"expected_version\":1}");
        await AssertCode(versionConflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(versionConflict) is not { } conflictDetails ||
            conflictDetails.GetProperty("server_version").GetInt64() != 2)
            throw new InvalidOperationException("Breakdown task version conflict details mismatch.");

        // Create the draft and replay it.
        var createKey = Guid.NewGuid();
        var created = await Create(client, token, t1, createKey, "{\"expected_version\":2}");
        Check(created, HttpStatusCode.Created);
        if (created.Headers.Location?.ToString() != "/ai/breakdowns/" + (await DraftId(created)))
            throw new InvalidOperationException("Breakdown location differs from contract.");
        var draft = await created.Content.ReadFromJsonAsync<AiBreakdownDraftView>()
            ?? throw new InvalidOperationException("Breakdown draft missing.");
        if (draft.TaskVersion != 2 || draft.Steps.Count != 3 ||
            !draft.Steps.Select(x => x.Position).SequenceEqual(new[] { 0, 1, 2 }) ||
            draft.SnapshotHash.Length < 16 || draft.ConfirmationToken.Length < 16 ||
            draft.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Breakdown draft projection mismatch.");
        var replay = await Create(client, token, t1, createKey, "{\"expected_version\":2}");
        Check(replay, HttpStatusCode.Created);
        if ((await replay.Content.ReadFromJsonAsync<AiBreakdownDraftView>())?.DraftId != draft.DraftId)
            throw new InvalidOperationException("Breakdown create replay mismatch.");
        await AssertCode(await Create(client, token, t1, createKey,
            "{\"expected_version\":2,\"guidance\":\"different\"}"), HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");

        // Edit: replace steps, rotate snapshot and token.
        var firstStep = Guid.NewGuid();
        var secondStep = Guid.NewGuid();
        var editKey = Guid.NewGuid();
        var editBody = "{\"snapshot_hash\":\"" + draft.SnapshotHash + "\",\"steps\":[" +
            StepJson(firstStep, "A", 10, 0) + "," + StepJson(secondStep, "B", null, 1) + "]}";
        var edited = await Edit(client, token, draft.DraftId, editKey, editBody);
        Check(edited, HttpStatusCode.OK);
        var editedDraft = await edited.Content.ReadFromJsonAsync<AiBreakdownDraftView>()
            ?? throw new InvalidOperationException("Edited draft missing.");
        if (editedDraft.DraftId != draft.DraftId || editedDraft.Steps.Count != 2 ||
            editedDraft.SnapshotHash == draft.SnapshotHash ||
            editedDraft.ConfirmationToken == draft.ConfirmationToken)
            throw new InvalidOperationException("Breakdown edit did not rotate snapshot/token.");
        var editReplay = await Edit(client, token, draft.DraftId, editKey, editBody);
        Check(editReplay, HttpStatusCode.OK);
        if ((await editReplay.Content.ReadFromJsonAsync<AiBreakdownDraftView>())?.SnapshotHash != editedDraft.SnapshotHash)
            throw new InvalidOperationException("Breakdown edit replay mismatch.");
        var staleEdit = await Edit(client, token, draft.DraftId, Guid.NewGuid(), editBody);
        await AssertCode(staleEdit, HttpStatusCode.Conflict, "BREAKDOWN_STALE");
        await AssertCode(await Edit(client, token, draft.DraftId, Guid.NewGuid(),
            "{\"snapshot_hash\":\"" + editedDraft.SnapshotHash + "\",\"steps\":[]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_BREAKDOWN");
        await AssertCode(await Edit(client, token, draft.DraftId, Guid.NewGuid(),
            "{\"snapshot_hash\":\"" + editedDraft.SnapshotHash + "\",\"steps\":[" +
            StepJson(Guid.NewGuid(), "A", null, 0) + "," + StepJson(Guid.NewGuid(), "B", null, 0) + "]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_BREAKDOWN");

        // Old token is invalidated; apply succeeds with the rotated snapshot and token.
        await AssertCode(await Apply(client, token, draft.DraftId, Guid.NewGuid(),
            "{\"snapshot_hash\":\"" + editedDraft.SnapshotHash + "\",\"confirmation_token\":\"" + draft.ConfirmationToken + "\"}"),
            HttpStatusCode.Conflict, "BREAKDOWN_STALE");
        var applyKey = Guid.NewGuid();
        var applyBody = "{\"snapshot_hash\":\"" + editedDraft.SnapshotHash +
            "\",\"confirmation_token\":\"" + editedDraft.ConfirmationToken + "\"}";
        var applied = await Apply(client, token, draft.DraftId, applyKey, applyBody);
        Check(applied, HttpStatusCode.OK);
        var appliedView = await applied.Content.ReadFromJsonAsync<ApplyBreakdownResponse>()
            ?? throw new InvalidOperationException("Applied response missing.");
        if (appliedView.Status != "applied" || appliedView.TaskId != t1 ||
            !appliedView.StepIds.SequenceEqual(new[] { firstStep, secondStep }))
            throw new InvalidOperationException("Applied breakdown response mismatch.");
        var applyReplay = await Apply(client, token, draft.DraftId, applyKey, applyBody);
        Check(applyReplay, HttpStatusCode.OK);
        if ((await applyReplay.Content.ReadFromJsonAsync<ApplyBreakdownResponse>())?.StepIds
            .SequenceEqual(new[] { firstStep, secondStep }) != true)
            throw new InvalidOperationException("Apply replay mismatch.");
        await AssertCode(await Apply(client, token, draft.DraftId, Guid.NewGuid(), applyBody),
            HttpStatusCode.Conflict, "BREAKDOWN_STALE");
        if (await Scalar(db, "SELECT count(*) FROM task_steps WHERE owner_id=$1 AND task_id=$2", owner, t1) != 2 ||
            await Scalar(db, "SELECT count(*) FROM task_steps WHERE id=$1 AND title='A' AND position=0", firstStep) != 1 ||
            await Scalar(db, "SELECT count(*) FROM ai_breakdown_drafts WHERE id=$1 AND status='applied'", draft.DraftId) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND version=2", t1) != 1)
            throw new InvalidOperationException("Breakdown apply did not create steps atomically.");

        // Expired drafts cannot be applied.
        var expired = await Create(client, token, t1, Guid.NewGuid(), "{\"expected_version\":2}");
        Check(expired, HttpStatusCode.Created);
        var expiredDraft = await expired.Content.ReadFromJsonAsync<AiBreakdownDraftView>()
            ?? throw new InvalidOperationException("Expired-candidate draft missing.");
        await Exec(db, "UPDATE ai_breakdown_drafts SET expires_at=now()-interval '1 minute' WHERE id=$1", expiredDraft.DraftId);
        await AssertCode(await Apply(client, token, expiredDraft.DraftId, Guid.NewGuid(),
            "{\"snapshot_hash\":\"" + expiredDraft.SnapshotHash +
            "\",\"confirmation_token\":\"" + expiredDraft.ConfirmationToken + "\"}"),
            HttpStatusCode.Conflict, "BREAKDOWN_STALE");

        // Owner isolation and deleted account.
        await AssertCode(await Edit(client, foreignToken, draft.DraftId, Guid.NewGuid(),
            "{\"snapshot_hash\":\"" + editedDraft.SnapshotHash + "\",\"steps\":[" + StepJson(Guid.NewGuid(), "X", null, 0) + "]}"),
            HttpStatusCode.NotFound, "BREAKDOWN_NOT_FOUND");
        await AssertCode(await Apply(client, foreignToken, draft.DraftId, Guid.NewGuid(), applyBody),
            HttpStatusCode.NotFound, "BREAKDOWN_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Create(client, foreignToken, tF, Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Accounting: one revision for the applied step batch, two step changes, four mutation records and one audit.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='step'", owner) != 2 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM ai_action_audits WHERE owner_id=$1 AND action_type='breakdown_apply'", owner) != 1)
            throw new InvalidOperationException("Breakdown writes did not keep revision, changes, mutations and audit consistent.");
        Console.WriteLine("PASS: breakdown draft create/edit/apply, token rotation, staleness, expiry, owner isolation, atomic steps");
    }

    private static string StepJson(Guid id, string title, short? estimate, int position) =>
        "{\"step_id\":\"" + id + "\",\"title\":\"" + title + "\",\"estimate_minutes\":" +
        (estimate?.ToString() ?? "null") + ",\"position\":" + position + "}";

    private static async Task<Guid> DraftId(HttpResponseMessage response)
    {
        var draft = await response.Content.ReadFromJsonAsync<AiBreakdownDraftView>()
            ?? throw new InvalidOperationException("Draft missing.");
        return draft.DraftId;
    }

    private static async Task<HttpResponseMessage> Create(HttpClient client, string? token, Guid taskId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/tasks/" + taskId + "/breakdowns")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Edit(HttpClient client, string? token, Guid draftId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/ai/breakdowns/" + draftId)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Apply(HttpClient client, string? token, Guid draftId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/breakdowns/" + draftId + "/apply")
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

    private sealed class BreakdownFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
