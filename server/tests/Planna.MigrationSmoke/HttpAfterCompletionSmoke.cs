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

internal static class HttpAfterCompletionSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var tF = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-after-one@example.test'),($2,'http-after-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'F','custom')",
            listA, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,created_at) VALUES($1,$2,$3,'After',now()-interval '30 days')", t1, owner, listA);
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
        await using var factory = new AfterFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await PutRule(client, null, t1, Guid.NewGuid(), RuleBody(0, "after_completion", null, 3,
            "Asia/Shanghai", "2026-11-10T09:00:00")), HttpStatusCode.Unauthorized);

        // Create the after-completion rule and validate mixing rules.
        var created = await PutRule(client, token, t1, Guid.NewGuid(),
            RuleBody(0, "after_completion", null, 3, "Asia/Shanghai", "2026-11-10T09:00:00"));
        Check(created, HttpStatusCode.OK);
        var rule = await created.Content.ReadFromJsonAsync<RecurrenceView>()
            ?? throw new InvalidOperationException("After-completion rule missing.");
        if (rule.Kind != "after_completion" || rule.IntervalDays != 3 || rule.Rrule is not null || rule.Version != 1)
            throw new InvalidOperationException("After-completion rule projection mismatch.");
        await AssertCode(await PutRule(client, token, t1, Guid.NewGuid(),
            RuleBody(1, "rrule", "FREQ=DAILY", 2, "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "INVALID_RECURRENCE");
        await AssertCode(await PutRule(client, token, t1, Guid.NewGuid(),
            RuleBody(1, "after_completion", null, 0, "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "INVALID_RECURRENCE");
        await AssertCode(await PutRule(client, token, t1, Guid.NewGuid(),
            RuleBody(1, "after_completion", "FREQ=DAILY", 3, "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "INVALID_RECURRENCE");
        await AssertCode(await PutRule(client, token, t1, Guid.NewGuid(),
            RuleBody(1, "legal_workday", null, null, "Asia/Shanghai", "2026-11-10T09:00:00")),
            HttpStatusCode.UnprocessableEntity, "UNSUPPORTED_RECURRENCE");

        // Complete the task: the next occurrence is the completion local date + 3 days.
        var completeKey = Guid.NewGuid();
        var completeBody = "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":\"2026-11-10T02:00:00Z\"}";
        var completed = await Complete(client, token, t1, completeKey, completeBody);
        Check(completed, HttpStatusCode.OK);
        var completedView = await completed.Content.ReadFromJsonAsync<TaskReadView>()
            ?? throw new InvalidOperationException("Completed after-completion task missing.");
        if (completedView.Status != "done" || completedView.Version != 2 || completedView.CompletedAt is null)
            throw new InvalidOperationException("After-completion task projection mismatch.");
        var first = await Occurrences(client, token, t1, "2026-11-12", "2026-11-14");
        if (first.Count != 1 ||
            first[0].OriginalLocalStart.Date != new DateTime(2026, 11, 13) ||
            first[0].DueAt != new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero) ||
            first[0].Status != "todo")
            throw new InvalidOperationException("After-completion next occurrence mismatch.");
        var replay = await Complete(client, token, t1, completeKey, completeBody);
        Check(replay, HttpStatusCode.OK);
        if ((await Occurrences(client, token, t1, "2026-11-12", "2026-11-14")).Count != 1)
            throw new InvalidOperationException("Replay duplicated the generated occurrence.");
        await AssertCode(await Complete(client, token, t1, Guid.NewGuid(), completeBody),
            HttpStatusCode.Conflict, "ALREADY_COMPLETED");

        // Reopen, complete again with a later completion date: a second occurrence is appended.
        Check(await Reopen(client, token, t1, "{\"expected_version\":2}"), HttpStatusCode.OK);
        var second = await Complete(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":3,\"occurrence_id\":null,\"completed_at\":\"2026-11-11T02:00:00Z\"}");
        Check(second, HttpStatusCode.OK);
        var occurrences = await Occurrences(client, token, t1, "2026-11-12", "2026-11-15");
        if (occurrences.Count != 2 ||
            !occurrences.Select(x => x.OriginalLocalStart.Date).SequenceEqual(
                new[] { new DateTime(2026, 11, 13), new DateTime(2026, 11, 14) }))
            throw new InvalidOperationException("After-completion second occurrence mismatch.");

        // Isolation and deleted accounts.
        await AssertCode(await Complete(client, token, tF, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Complete(client, foreignToken, tF, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: rule write, completion, reopen and second completion.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 4 ||
            await Scalar(db, "SELECT count(*) FROM task_occurrences WHERE owner_id=$1", owner) != 2 ||
            await Scalar(db, "SELECT count(*) FROM recurrences WHERE owner_id=$1 AND kind='after_completion' AND interval_days=3", owner) != 1)
            throw new InvalidOperationException("After-completion writes did not keep revision and occurrence state consistent.");
        Console.WriteLine("PASS: after_completion rule, next occurrence generation, replay, reopen, owner isolation");
    }

    private static string RuleBody(long expectedVersion, string kind, string? rrule, int? intervalDays,
        string timeZone, string dtstart) =>
        "{\"expected_version\":" + expectedVersion + ",\"kind\":\"" + kind + "\"," +
        "\"rrule\":" + (rrule is null ? "null" : "\"" + rrule + "\"") + "," +
        "\"interval_days\":" + (intervalDays?.ToString() ?? "null") + "," +
        "\"time_zone\":\"" + timeZone + "\",\"dtstart_local\":\"" + dtstart + "\"}";

    private static async Task<List<OccurrenceView>> Occurrences(HttpClient client, string? token, Guid taskId,
        string from, string to)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/tasks/" + taskId + "/occurrences?from=" + from + "&to=" + to);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Check(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OccurrenceListView>()
            ?? throw new InvalidOperationException("Occurrence list missing.")).Items.ToList();
    }

    private static async Task<HttpResponseMessage> PutRule(HttpClient client, string? token, Guid taskId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/tasks/" + taskId + "/recurrence")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Complete(HttpClient client, string? token, Guid taskId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + taskId + "/complete")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Reopen(HttpClient client, string? token, Guid taskId, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + taskId + "/reopen")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
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

    private sealed class AfterFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
