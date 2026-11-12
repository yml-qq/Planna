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
using Planna.Application.Export;
using Planna.Application.Identity;

internal static class HttpExportSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var list = Guid.NewGuid();
        var deletedList = Guid.NewGuid();
        var task = Guid.NewGuid();
        var deletedTask = Guid.NewGuid();
        var step = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var reminder = Guid.NewGuid();
        var block = Guid.NewGuid();
        var habit = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'http-export-one@example.test'),($2,'http-export-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name,kind) VALUES($1,$2,'A','custom'),($3,$2,'Gone','custom')", list, owner, deletedList);
        await Exec(db, "UPDATE task_lists SET deleted_at=now() WHERE id=$1", deletedList);
        await Exec(db, """
            INSERT INTO tasks(id,owner_id,list_id,title,description,priority,due_at,version)
            VALUES($1,$2,$3,'Export task','desc',1,'2026-12-01T10:00:00Z',2),($4,$2,$3,'Deleted task','',0,NULL,1)
            """, task, owner, list, deletedTask);
        await Exec(db, "UPDATE tasks SET deleted_at=now() WHERE id=$1", deletedTask);
        await Exec(db, "INSERT INTO task_steps(id,owner_id,task_id,title,is_done) VALUES($1,$2,$3,'Step',false)", step, owner, task);
        await Exec(db, "INSERT INTO tags(id,owner_id,name) VALUES($1,$2,'Tag')", tag, owner);
        await Exec(db, "INSERT INTO reminders(id,owner_id,task_id,scheduled_at) VALUES($1,$2,$3,'2026-12-01T09:00:00Z')", reminder, owner, task);
        await Exec(db, "INSERT INTO task_time_blocks(id,owner_id,task_id,start_at,end_at) VALUES($1,$2,$3,'2026-12-01T10:00:00Z','2026-12-01T10:30:00Z')", block, owner, task);
        await Exec(db, "INSERT INTO ai_consents(owner_id,allowed_scopes) VALUES($1,ARRAY['task_drafts'])", owner);
        await Exec(db, "INSERT INTO habits(id,owner_id,title,schedule_type,target_quantity,unit,time_zone) VALUES($1,$2,'Read','daily',2,'pages','Asia/Shanghai')",
            habit, owner);
        await Exec(db, "INSERT INTO habit_logs(owner_id,habit_id,log_date,quantity,status) VALUES($1,$2,'2026-11-01',2,'done')",
            owner, habit);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new ExportFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Create(client, null, Guid.NewGuid()), HttpStatusCode.Unauthorized);
        Check(await List(client, null), HttpStatusCode.Unauthorized);

        var createKey = Guid.NewGuid();
        var created = await Create(client, token, createKey);
        Check(created, HttpStatusCode.Accepted);
        var job = await created.Content.ReadFromJsonAsync<AccountExportJobView>()
            ?? throw new InvalidOperationException("Export job missing.");
        if (job.Status != "ready" || job.SnapshotAt is null || job.ExpiresAt is null ||
            job.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Export job projection mismatch.");
        var replay = await Create(client, token, createKey);
        Check(replay, HttpStatusCode.Accepted);
        if ((await replay.Content.ReadFromJsonAsync<AccountExportJobView>())?.ExportId != job.ExportId)
            throw new InvalidOperationException("Export replay changed the job.");

        var listing = await (await List(client, token)).Content.ReadFromJsonAsync<AccountExportJobList>()
            ?? throw new InvalidOperationException("Export list missing.");
        if (listing.Items.Count != 1 || listing.Items[0].ExportId != job.ExportId)
            throw new InvalidOperationException("Export list mismatch.");
        var fetched = await (await Get(client, token, job.ExportId)).Content.ReadFromJsonAsync<AccountExportJobView>()
            ?? throw new InvalidOperationException("Export status missing.");
        if (fetched.Status != "ready") throw new InvalidOperationException("Export status mismatch.");

        var download = await Download(client, token, job.ExportId);
        Check(download, HttpStatusCode.OK);
        if (!download.Content.Headers.ContentDisposition?.FileName?.Contains(job.ExportId.ToString()) ?? true)
            throw new InvalidOperationException("Export attachment filename missing.");
        using var payload = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        var data = root.GetProperty("data");
        if (root.GetProperty("schema_version").GetString() != "account-export-v2" ||
            root.GetProperty("source").GetString() != "cloud" ||
            root.GetProperty("pending_mutation_ids").GetArrayLength() != 0 ||
            data.GetProperty("lists").GetArrayLength() != 1 ||
            data.GetProperty("tasks").GetArrayLength() != 1 ||
            data.GetProperty("steps").GetArrayLength() != 1 ||
            data.GetProperty("tags").GetArrayLength() != 1 ||
            data.GetProperty("reminders").GetArrayLength() != 1 ||
            data.GetProperty("time_blocks").GetArrayLength() != 1 ||
            data.GetProperty("ai_memories").GetArrayLength() != 0 ||
            data.GetProperty("habits").GetArrayLength() != 1 ||
            data.GetProperty("habit_logs").GetArrayLength() != 1 ||
            data.GetProperty("ai_consents")[0].GetProperty("allowed_scopes")[0].GetString() != "task_drafts")
            throw new InvalidOperationException("Export payload content mismatch.");
        var exportedHabit = data.GetProperty("habits")[0];
        if (exportedHabit.GetProperty("title").GetString() != "Read" ||
            exportedHabit.GetProperty("target_quantity").GetInt64() != 2 ||
            exportedHabit.GetProperty("unit").GetString() != "pages" ||
            data.GetProperty("habit_logs")[0].GetProperty("log_date").GetString() != "2026-11-01" ||
            data.GetProperty("habit_logs")[0].GetProperty("quantity").GetInt64() != 2)
            throw new InvalidOperationException("Exported habit projection mismatch.");
        var exportedTask = data.GetProperty("tasks")[0];
        if (exportedTask.GetProperty("title").GetString() != "Export task" ||
            exportedTask.GetProperty("version").GetInt64() != 2 ||
            exportedTask.GetProperty("deleted_at").ValueKind != JsonValueKind.Null ||
            exportedTask.GetProperty("description").GetString() != "desc")
            throw new InvalidOperationException("Exported task projection mismatch.");

        await AssertCode(await Download(client, foreignToken, job.ExportId),
            HttpStatusCode.NotFound, "EXPORT_NOT_FOUND");
        await Exec(db, "UPDATE account_exports SET expires_at=now()-interval '1 minute' WHERE id=$1", job.ExportId);
        await AssertCode(await Download(client, token, job.ExportId), HttpStatusCode.Gone, "EXPORT_EXPIRED");
        var pending = Guid.NewGuid();
        await Exec(db, "INSERT INTO account_exports(id,owner_id,status,requested_at) VALUES($1,$2,'pending',now())", pending, owner);
        await AssertCode(await Download(client, token, pending), HttpStatusCode.Conflict, "EXPORT_NOT_READY");
        await AssertCode(await Download(client, token, Guid.Empty), HttpStatusCode.BadRequest, "INVALID_EXPORT_ID");
        await AssertCode(await Get(client, token, Guid.NewGuid()), HttpStatusCode.NotFound, "EXPORT_NOT_FOUND");

        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await Create(client, foreignToken, Guid.NewGuid()), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Exports are read-only snapshots: no revision and no business sync changes are produced.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM account_exports WHERE owner_id=$1 AND payload IS NOT NULL", owner) != 1)
            throw new InvalidOperationException("Export mutated business state or lost its payload/idempotency record.");
        Console.WriteLine("PASS: cloud export snapshot, download content, replay, expiry, owner isolation, read-only");
    }

    private static async Task<HttpResponseMessage> Create(HttpClient client, string? token, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/me/exports");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> List(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/exports");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/exports/" + id);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Download(HttpClient client, string? token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/exports/" + id + "/download");
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

    private sealed class ExportFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
