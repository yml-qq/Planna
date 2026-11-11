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

internal static class HttpTaskReadSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var listA1 = Guid.NewGuid();
        var listA2 = Guid.NewGuid();
        var deletedList = Guid.NewGuid();
        var foreignList = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-read-one@example.test'),($2,'http-read-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A1','custom'),($3,$2,'A2','custom'),($4,$2,'Deleted','custom'),($5,$6,'Foreign','custom')",
            listA1, owner, listA2, deletedList, foreignList, foreign);
        await Exec(db, "UPDATE task_lists SET deleted_at=now() WHERE id=$1", deletedList);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var t4 = Guid.NewGuid();
        var t5 = Guid.NewGuid();
        var t6 = Guid.NewGuid();
        var t7 = Guid.NewGuid();
        var foreignTask = Guid.NewGuid();
        // created_at strictly increases from t1 to t7, so expected page order is t7,t6,t4,t3,t2,t1.
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at) VALUES($1,$2,$3,'T1','todo',now()-interval '1 hour')", t1, owner, listA1);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at) VALUES($1,$2,$3,'T2','todo',now()-interval '50 minutes')", t2, owner, listA1);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at,completed_at) VALUES($1,$2,$3,'T3','done',now()-interval '40 minutes',now()-interval '10 minutes')", t3, owner, listA2);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at) VALUES($1,$2,$3,'T4','archived',now()-interval '30 minutes')", t4, owner, listA2);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at,deleted_at) VALUES($1,$2,$3,'T5','todo',now()-interval '20 minutes',now())", t5, owner, listA1);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at,version) VALUES($1,$2,$3,'T6','todo',now()-interval '10 minutes',3)", t6, owner, listA1);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status,created_at) VALUES($1,$2,$3,'T7','in_progress',now()-interval '5 minutes')", t7, owner, listA1);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title,status) VALUES($1,$2,$3,'Foreign','todo')", foreignTask, foreign, foreignList);
        await Exec(db, "UPDATE tasks SET due_at=now()+interval '1 day' WHERE id=$1", t1);
        await Exec(db, "UPDATE tasks SET due_local_date='2026-11-12', all_day=true WHERE id=$1", t2);
        await Exec(db, "UPDATE tasks SET estimate_minutes=25 WHERE id=$1", t6);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new ReadFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Get(client, null), HttpStatusCode.Unauthorized);
        foreach (var limit in new[] { "0", "201", "abc" })
            await AssertCode(await Get(client, token, $"limit={limit}"), HttpStatusCode.BadRequest, "INVALID_LIMIT");
        await AssertCode(await Get(client, token, "limit=1&limit=2"), HttpStatusCode.BadRequest, "INVALID_LIMIT");
        foreach (var cursor in new[] { "garbage", Encode("abc:def"), Encode("12:def") })
            await AssertCode(await Get(client, token, $"cursor={Uri.EscapeDataString(cursor)}"), HttpStatusCode.BadRequest, "INVALID_CURSOR");
        foreach (var list in new[] { "not-a-uuid", "" })
            await AssertCode(await Get(client, token, $"list_id={Uri.EscapeDataString(list)}"), HttpStatusCode.BadRequest, "INVALID_LIST_ID");
        foreach (var status in new[] { "weird", "TODO" })
            await AssertCode(await Get(client, token, $"status={status}"), HttpStatusCode.BadRequest, "INVALID_STATUS");
        await AssertCode(await Get(client, token, $"list_id={foreignList}"), HttpStatusCode.NotFound, "LIST_NOT_FOUND");
        await AssertCode(await Get(client, token, $"list_id={deletedList}"), HttpStatusCode.NotFound, "LIST_NOT_FOUND");

        var walk = new List<TaskReadView>();
        string? cursorValue = null;
        do
        {
            var response = await Get(client, token, "limit=1" + (cursorValue is null ? "" : "&cursor=" + Uri.EscapeDataString(cursorValue)));
            Check(response, HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<TaskReadPage>()
                ?? throw new InvalidOperationException("Task page missing.");
            if (page.Items.Count != 1) throw new InvalidOperationException("Unexpected task page size.");
            walk.AddRange(page.Items);
            cursorValue = page.NextCursor;
        } while (cursorValue is not null);
        var ids = walk.Select(x => x.Id).ToList();
        if (ids.Count != 6 || ids.Distinct().Count() != 6 ||
            !ids.SequenceEqual(new[] { t7, t6, t4, t3, t2, t1 }))
            throw new InvalidOperationException("Task keyset pagination duplicated, missed or misordered items.");
        if (walk.Any(x => x.Id == t5 || x.Id == foreignTask))
            throw new InvalidOperationException("Deleted or foreign tasks leaked into pages.");
        var t1view = walk.Single(x => x.Id == t1);
        if (t1view.DueAt is null || t1view.AllDay || t1view.TimeZone != "Asia/Shanghai")
            throw new InvalidOperationException("Task projection lost due_at or time zone.");
        var t2view = walk.Single(x => x.Id == t2);
        if (t2view.DueLocalDate != new DateOnly(2026, 11, 12) || !t2view.AllDay)
            throw new InvalidOperationException("All-day task projection mismatch.");
        var t3view = walk.Single(x => x.Id == t3);
        if (t3view.Status != "done" || t3view.CompletedAt is null)
            throw new InvalidOperationException("Completed task projection mismatch.");
        var t6view = walk.Single(x => x.Id == t6);
        if (t6view.Version != 3 || t6view.EstimateMinutes != 25 || t6view.Status != "todo")
            throw new InvalidOperationException("Task version or estimate projection mismatch.");

        var defaultPage = await Page(client, token, null);
        if (defaultPage.Items.Count != 6 || defaultPage.NextCursor is not null)
            throw new InvalidOperationException("Default limit page differs from full result.");
        Check(await Get(client, token, "limit=200"), HttpStatusCode.OK);
        var empty = await Page(client, token, "status=done");
        if (empty.Items.Count != 1 || empty.Items[0].Id != t3 || empty.NextCursor is not null)
            throw new InvalidOperationException("Status filter failed.");
        if ((await Page(client, token, "status=archived")).Items.Single().Id != t4 ||
            (await Page(client, token, "status=in_progress")).Items.Single().Id != t7)
            throw new InvalidOperationException("Status filter mismatch.");
        if (!(await Page(client, token, "status=todo")).Items.Select(x => x.Id).SequenceEqual(new[] { t6, t2, t1 }))
            throw new InvalidOperationException("Todo filter mismatch.");
        if (!(await Page(client, token, $"list_id={listA2}")).Items.Select(x => x.Id).SequenceEqual(new[] { t4, t3 }))
            throw new InvalidOperationException("List filter mismatch.");

        var filtered = new List<TaskReadView>();
        string? filteredCursor = null;
        do
        {
            var response = await Get(client, token, "limit=1&list_id=" + listA1 +
                (filteredCursor is null ? "" : "&cursor=" + Uri.EscapeDataString(filteredCursor)));
            Check(response, HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<TaskReadPage>()
                ?? throw new InvalidOperationException("Filtered task page missing.");
            if (page.Items.Count != 1) throw new InvalidOperationException("Unexpected filtered page size.");
            filtered.AddRange(page.Items);
            filteredCursor = page.NextCursor;
        } while (filteredCursor is not null);
        if (!filtered.Select(x => x.Id).SequenceEqual(new[] { t7, t6, t2, t1 }))
            throw new InvalidOperationException("Filtered keyset pagination misordered items.");

        var foreignPage = await Page(client, foreignToken, null);
        if (foreignPage.Items.Count != 1 || foreignPage.Items[0].Id != foreignTask)
            throw new InvalidOperationException("Foreign owner saw wrong tasks.");

        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Get(client, foreignToken), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");
        Console.WriteLine("PASS: task read auth, limit/cursor/filter errors, keyset pagination, owner isolation, projections");
    }

    private static async Task<TaskReadPage> Page(HttpClient client, string? token, string? query)
    {
        var response = await Get(client, token, query);
        Check(response, HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<TaskReadPage>()
            ?? throw new InvalidOperationException("Task page missing.");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tasks" + (query is null ? "" : "?" + query));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task AddSession(NpgsqlConnection db, Guid owner, Guid id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        await Exec(db, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3)",
            id, owner, hash);
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

    private sealed class ReadFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
