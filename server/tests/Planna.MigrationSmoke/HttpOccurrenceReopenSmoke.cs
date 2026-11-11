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

internal static class HttpOccurrenceReopenSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var list = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var tF = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-reopen-one@example.test'),($2,'http-reopen-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$4,'F','custom')",
            list, owner, foreignList, foreign);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Recurring'),($4,$2,$3,'Plain')", t1, owner, list, t2);
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
        await using var factory = new ReopenFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Reopen(client, null, t1, Guid.NewGuid(), "{\"expected_version\":1}"), HttpStatusCode.Unauthorized);

        // Rule + occurrence completion.
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = today.ToString("yyyy-MM-dd");
        var dtstart = today.AddDays(-1).ToString("yyyy-MM-dd") + "T09:00:00";
        Check(await PutRule(client, token, t1, "{\"expected_version\":0,\"kind\":\"rrule\",\"rrule\":\"FREQ=DAILY;INTERVAL=1\",\"time_zone\":\"Asia/Shanghai\",\"dtstart_local\":\"" + dtstart + "\"}"),
            HttpStatusCode.OK);
        var occurrences = await Occurrences(client, token, t1, from, to);
        var target = occurrences[0];
        Check(await Complete(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":\"" + target.Id + "\",\"completed_at\":null}"), HttpStatusCode.OK);

        // Reopen the occurrence: done -> todo, clear completed_at, version+1.
        var reopenKey = Guid.NewGuid();
        var reopenBody = "{\"expected_version\":1,\"occurrence_id\":\"" + target.Id + "\"}";
        var reopened = await Reopen(client, token, t1, reopenKey, reopenBody);
        Check(reopened, HttpStatusCode.OK);
        var reopenedView = await reopened.Content.ReadFromJsonAsync<OccurrenceView>()
            ?? throw new InvalidOperationException("Reopened occurrence missing.");
        if (reopenedView.Id != target.Id || reopenedView.Status != "todo" ||
            reopenedView.CompletedAt is not null || reopenedView.Version != 3)
            throw new InvalidOperationException("Reopened occurrence projection mismatch.");
        var replay = await Reopen(client, token, t1, reopenKey, reopenBody);
        Check(replay, HttpStatusCode.OK);
        if (await replay.Content.ReadFromJsonAsync<OccurrenceView>() != reopenedView)
            throw new InvalidOperationException("Occurrence reopen replay changed the response.");
        await AssertCode(await Reopen(client, token, t1, Guid.NewGuid(), reopenBody),
            HttpStatusCode.Conflict, "NOT_COMPLETED");
        await AssertCode(await Reopen(client, token, t1, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":\"" + Guid.NewGuid() + "\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_OCCURRENCE");
        await AssertCode(await Reopen(client, token, t1, Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.UnprocessableEntity, "RECURRING_TASK_REQUIRES_OCCURRENCE");

        // Plain tasks keep task-level reopen; occurrence_id is rejected.
        Check(await Complete(client, token, t2, Guid.NewGuid(),
            "{\"expected_version\":1,\"occurrence_id\":null,\"completed_at\":null}"), HttpStatusCode.OK);
        var taskReopen = await Reopen(client, token, t2, Guid.NewGuid(), "{\"expected_version\":2}");
        Check(taskReopen, HttpStatusCode.OK);
        if ((await taskReopen.Content.ReadFromJsonAsync<TaskReadView>())?.Status != "todo")
            throw new InvalidOperationException("Plain task reopen mismatch.");
        await AssertCode(await Reopen(client, token, t2, Guid.NewGuid(),
            "{\"expected_version\":3,\"occurrence_id\":\"" + Guid.NewGuid() + "\"}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_OCCURRENCE");

        // Owner isolation and deleted account.
        await AssertCode(await Reopen(client, foreignToken, t1, Guid.NewGuid(), reopenBody),
            HttpStatusCode.NotFound, "TASK_NOT_FOUND");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Reopen(client, foreignToken, tF, Guid.NewGuid(), "{\"expected_version\":1}"),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Atomic accounting: rule, occurrence complete and occurrence reopen, plus plain task completion/reopen.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM task_occurrences WHERE id=$1 AND status='todo' AND completed_at IS NULL AND version=3", target.Id) != 1 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='occurrence'", owner) != 2)
            throw new InvalidOperationException("Occurrence reopen did not keep revision and change records consistent.");
        Console.WriteLine("PASS: occurrence reopen, replay, state guards, task-level reopen, owner isolation");
    }

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

    private static async Task<HttpResponseMessage> PutRule(HttpClient client, string? token, Guid taskId, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/tasks/" + taskId + "/recurrence")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
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

    private static async Task<HttpResponseMessage> Reopen(HttpClient client, string? token, Guid taskId,
        Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks/" + taskId + "/reopen")
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

    private sealed class ReopenFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
