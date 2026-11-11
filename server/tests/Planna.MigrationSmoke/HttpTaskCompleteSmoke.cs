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

internal static class HttpTaskCompleteSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-complete-one@example.test'),($2,'http-complete-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'Foreign','custom')",
            listA, owner, foreignList, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var c3 = Guid.NewGuid();
        var c4 = Guid.NewGuid();
        var c5 = Guid.NewGuid();
        var c6 = Guid.NewGuid();
        var c7 = Guid.NewGuid();
        var c8 = Guid.NewGuid();
        var c9 = Guid.NewGuid();
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version,created_at) VALUES($1,$2,$3,'C1','todo',2,now()-interval '1 hour')", c1, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C2','in_progress',1)", c2, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version,completed_at) VALUES($1,$2,$3,'C3','done',3,now()-interval '10 minutes')", c3, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C4','archived',1)", c4, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version,deleted_at) VALUES($1,$2,$3,'C5','todo',1,now())", c5, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C6','todo',1)", c6, foreign, foreignList);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C7','todo',1)", c7, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C8','todo',1)", c8, owner, listA);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,version) VALUES($1,$2,$3,'C9','todo',1)", c9, owner, listA);
        await Exec(db, "INSERT INTO recurrences(owner_id,task_id,kind,rrule,dtstart_local,time_zone) VALUES($1,$2,'rrule','FREQ=DAILY',now(),'Asia/Shanghai')", owner, c7);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new CompleteFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        // Authentication and identifier errors.
        Check(await Complete(client, null, c1, Guid.NewGuid(), "{}"), HttpStatusCode.Unauthorized);
        Check(await Reopen(client, null, c3, Guid.NewGuid(), "{\"expected_version\":3}"), HttpStatusCode.Unauthorized);
        await AssertCode(await Complete(client, token, Guid.Empty, Guid.NewGuid(), "{}"), HttpStatusCode.BadRequest, "INVALID_TASK_ID");
        await AssertCode(await Reopen(client, token, Guid.Empty, Guid.NewGuid(), "{\"expected_version\":3}"), HttpStatusCode.BadRequest, "INVALID_TASK_ID");
        await AssertCode(await Complete(client, token, c1, null,
            "{\"expected_version\":2,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        Check(await Complete(client, token, c1, Guid.NewGuid(), "{}"), HttpStatusCode.BadRequest);

        // Happy complete with a past completed_at, replay and key reuse.
        var past = DateTimeOffset.UtcNow.AddMinutes(-30);
        var key1 = Guid.NewGuid();
        var happyBody = $"{{\"expected_version\":2,\"occurrence_id\":null,\"completed_at\":\"{past:O}\"}}";
        var completed = await Complete(client, token, c1, key1, happyBody);
        Check(completed, HttpStatusCode.OK);
        var completedView = await completed.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Completed task missing.");
        if (completedView.Status != "done" || completedView.Version != 3 ||
            completedView.CompletedAt is null ||
            Math.Abs((completedView.CompletedAt.Value - past).TotalMinutes) >= 1)
            throw new InvalidOperationException("Completed task projection mismatch.");
        var replay = await Complete(client, token, c1, key1, happyBody);
        Check(replay, HttpStatusCode.OK);
        if (await replay.Content.ReadFromJsonAsync<TaskReadView>() != completedView)
            throw new InvalidOperationException("Complete replay changed the response.");
        await AssertCode(await Complete(client, token, c1, key1,
            $"{{\"expected_version\":2,\"occurrence_id\":null,\"completed_at\":\"{past.AddMinutes(-1):O}\"}}"),
            HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        var conflict = await Complete(client, token, c1, Guid.NewGuid(),
            $"{{\"expected_version\":2,\"occurrence_id\":null,\"completed_at\":\"{past:O}\"}}");
        await AssertCode(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT");
        if (await Details(conflict) is not { } d ||
            d.GetProperty("server_version").GetInt64() != 3 ||
            d.GetProperty("entity_id").GetGuid() != c1)
            throw new InvalidOperationException("Complete conflict details mismatch.");
        await AssertCode(await Complete(client, token, c1, Guid.NewGuid(),
            $"{{\"expected_version\":3,\"occurrence_id\":null,\"completed_at\":\"{past:O}\"}}"),
            HttpStatusCode.Conflict, "ALREADY_COMPLETED");

        // Trusted time: missing completed_at uses server time; future values clamp to now.
        var missing = await Complete(client, token, c8, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}");
        Check(missing, HttpStatusCode.OK);
        var missingView = await missing.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Server-time completion missing.");
        if (missingView.Version != 2 || missingView.Status != "done" ||
            missingView.CompletedAt is null || missingView.CompletedAt > DateTimeOffset.UtcNow.AddSeconds(10))
            throw new InvalidOperationException("Missing completed_at was not filled with server time.");
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var clamped = await Complete(client, token, c9, Guid.NewGuid(),
            $"{{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":\"{future:O}\"}}");
        Check(clamped, HttpStatusCode.OK);
        var clampedView = await clamped.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Clamped completion missing.");
        if (clampedView.Version != 2 || clampedView.Status != "done" ||
            clampedView.CompletedAt is null || clampedView.CompletedAt > DateTimeOffset.UtcNow.AddSeconds(10))
            throw new InvalidOperationException("Future completed_at was not clamped to server time.");

        // Occurrence and state guards.
        await AssertCode(await Complete(client, token, c2, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":\"" + Guid.NewGuid() + "\",\"completed_at\":null}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_OCCURRENCE");
        await AssertCode(await Complete(client, token, c7, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.UnprocessableEntity, "RECURRING_TASK_REQUIRES_OCCURRENCE");
        await AssertCode(await Complete(client, token, c4, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TASK_STATE");
        await AssertCode(await Reopen(client, token, c4, Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_TASK_STATE");
        await AssertCode(await Complete(client, token, c5, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await AssertCode(await Complete(client, token, c6, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Concurrent completes serialize: one wins, one conflicts.
        var race = await Task.WhenAll(
            Complete(client, token, c2, Guid.NewGuid(), "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            Complete(client, token, c2, Guid.NewGuid(), "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"));
        if (race.Count(x => x.StatusCode == HttpStatusCode.OK) != 1 ||
            race.Count(x => x.StatusCode == HttpStatusCode.Conflict) != 1)
            throw new InvalidOperationException("Concurrent complete was not serialized.");

        // Reopen: done -> todo with cleared completed_at, replay and state guards.
        var reopenKey = Guid.NewGuid();
        var reopened = await Reopen(client, token, c3, reopenKey, "{\"expected_version\":3}");
        Check(reopened, HttpStatusCode.OK);
        var reopenedView = await reopened.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Reopened task missing.");
        if (reopenedView.Version != 4 || reopenedView.Status != "todo" || reopenedView.CompletedAt is not null)
            throw new InvalidOperationException("Reopen did not reset status and completed_at.");
        Check(await Reopen(client, token, c3, reopenKey, "{\"expected_version\":3}"), HttpStatusCode.OK);
        await AssertCode(await Reopen(client, token, c3, Guid.NewGuid(), "{\"expected_version\":4}"),
            HttpStatusCode.Conflict, "NOT_COMPLETED");
        var c1reopen = await Reopen(client, token, c1, Guid.NewGuid(), "{\"expected_version\":3}");
        Check(c1reopen, HttpStatusCode.OK);
        var c1view = await c1reopen.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("C1 reopen missing.");
        if (c1view.Version != 4 || c1view.Status != "todo" || c1view.CompletedAt is not null)
            throw new InvalidOperationException("C1 reopen state mismatch.");
        await AssertCode(await Reopen(client, token, c1, Guid.NewGuid(), "{\"expected_version\":4}"),
            HttpStatusCode.Conflict, "NOT_COMPLETED");
        await AssertCode(await Reopen(client, token, c5, Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");

        // Foreign owner completes their own task; deleted account is rejected.
        Check(await Complete(client, foreignToken, c6, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"), HttpStatusCode.OK);
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Complete(client, foreignToken, c6, Guid.NewGuid(),
            "{\"expected_version\":2,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: 6 owner writes, 1 foreign write.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 6 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND status='todo' AND completed_at IS NULL AND version=4", c1) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND status='todo' AND completed_at IS NULL AND version=4", c3) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND status='done' AND completed_at IS NOT NULL", c8) != 1 ||
            await Scalar(db, "SELECT count(*) FROM tasks WHERE id=$1 AND status='done' AND completed_at IS NOT NULL AND completed_at<=now()", c9) != 1 ||
            await Scalar(db, "SELECT count(*) FROM recurrences WHERE owner_id=$1 AND task_id=$2", owner, c7) != 1 ||
            await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", foreign) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", foreign) != 1)
            throw new InvalidOperationException("Complete/reopen did not keep revision, sync changes and mutation records atomic.");
        Console.WriteLine("PASS: task complete/reopen state machine, trusted time, replay, concurrency, owner isolation");
    }

    private static async Task<HttpResponseMessage> Complete(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + id + "/complete")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Reopen(HttpClient client, string? token, Guid id, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + id + "/reopen")
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

    private sealed class CompleteFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
