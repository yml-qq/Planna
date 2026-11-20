using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;

internal static class HttpNoteSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'note-one@example.test'),($2,'note-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        var listId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name) VALUES($1,$2,'Inbox')", listId, owner);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Linked task')",
            taskId, owner, listId);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new NoteFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Send(client, null, HttpMethod.Get, "/notes", null, null, null),
            HttpStatusCode.Unauthorized);

        var createKey = Guid.NewGuid();
        var create = await Send(client, token, HttpMethod.Post, "/notes",
            "{\"title\":\"Meeting\",\"content\":\"- agenda\\n- owner\"}", createKey, null);
        Check(create, HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var noteId = created.RootElement.GetProperty("id").GetString()!;
        if (created.RootElement.GetProperty("version").GetInt64() != 1)
            throw new InvalidOperationException("Note create mismatch.");
        var replay = await Send(client, token, HttpMethod.Post, "/notes",
            "{\"title\":\"Meeting\",\"content\":\"- agenda\\n- owner\"}", createKey, null);
        Check(replay, HttpStatusCode.OK);
        var reused = await Send(client, token, HttpMethod.Post, "/notes", "{\"content\":\"Other\"}",
            createKey, null);
        Check(reused, HttpStatusCode.Conflict);

        var percent = await Send(client, token, HttpMethod.Post, "/notes",
            "{\"content\":\"50% done\"}", Guid.NewGuid(), null);
        Check(percent, HttpStatusCode.Created);
        var linked = await Send(client, token, HttpMethod.Post, "/notes",
            "{\"title\":\"Task note\",\"content\":\"context\",\"task_id\":\"" + taskId + "\"}",
            Guid.NewGuid(), null);
        Check(linked, HttpStatusCode.Created);

        var all = await Send(client, token, HttpMethod.Get, "/notes?limit=10", null, null, null);
        Check(all, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await all.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 3)
                throw new InvalidOperationException("Note list mismatch.");
        }
        var search = await Send(client, token, HttpMethod.Get, "/notes?query=%25", null, null, null);
        Check(search, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await search.Content.ReadAsStringAsync()))
        {
            var items = document.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 1 ||
                items[0].GetProperty("content").GetString() != "50% done")
                throw new InvalidOperationException("Note like escaping mismatch.");
        }
        var byTask = await Send(client, token, HttpMethod.Get, "/notes?task_id=" + taskId, null, null, null);
        Check(byTask, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await byTask.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Note task filter mismatch.");
        }

        var update = await Send(client, token, HttpMethod.Patch, "/notes/" + noteId,
            "{\"expected_version\":1,\"patch\":{\"content\":\"- agenda\\n- owner\\n- date\"}}",
            Guid.NewGuid(), null);
        Check(update, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await update.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("version").GetInt64() != 2 ||
                !document.RootElement.GetProperty("content").GetString()!.Contains("date"))
                throw new InvalidOperationException("Note update mismatch.");
        }
        var stale = await Send(client, token, HttpMethod.Patch, "/notes/" + noteId,
            "{\"expected_version\":1,\"patch\":{\"title\":\"Stale\"}}", Guid.NewGuid(), null);
        Check(stale, HttpStatusCode.Conflict);
        if (await ErrorCode(stale) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Note version conflict mismatch.");

        Check(await Send(client, foreignToken, HttpMethod.Get, "/notes/" + noteId, null, null, null),
            HttpStatusCode.NotFound);
        await AssertStatus(client, token, HttpMethod.Post, "/notes", "{\"title\":\"  \",\"content\":\"\"}",
            Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/notes",
            "{\"content\":\"x\",\"task_id\":\"" + Guid.NewGuid() + "\"}", Guid.NewGuid(), null,
            HttpStatusCode.NotFound);
        Check(await Send(client, token, HttpMethod.Get, "/notes?limit=0", null, null, null),
            HttpStatusCode.BadRequest);
        Check(await Send(client, token, HttpMethod.Get, "/notes?query=" + new string('x', 101),
            null, null, null), HttpStatusCode.BadRequest);

        var delete = await Send(client, token, HttpMethod.Delete, "/notes/" + noteId, null,
            Guid.NewGuid(), "\"v2\"");
        Check(delete, HttpStatusCode.NoContent);
        Check(await Send(client, token, HttpMethod.Get, "/notes/" + noteId, null, null, null),
            HttpStatusCode.NotFound);
        Console.WriteLine("PASS: notes create/replay/list/search/filter/update/delete, escaping and owner isolation");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string? token, HttpMethod method,
        string path, string? body, Guid? mutation, string? ifMatch)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (mutation is { } key) request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AssertStatus(HttpClient client, string token, HttpMethod method, string path,
        string? body, Guid mutation, string? ifMatch, HttpStatusCode expected)
    {
        var response = await Send(client, token, method, path, body, mutation, ifMatch);
        Check(response, expected);
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
    }

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

    private sealed class NoteFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
