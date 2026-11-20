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

internal static class HttpReminderSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'reminder-one@example.test'),($2,'reminder-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        var listId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name) VALUES($1,$2,'Inbox')", listId, owner);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Write report')",
            taskId, owner, listId);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new ReminderFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Send(client, null, HttpMethod.Get, "/tasks/" + taskId + "/reminders", null, null, null),
            HttpStatusCode.Unauthorized);

        var scheduled = DateTimeOffset.UtcNow.AddHours(1);
        var createKey = Guid.NewGuid();
        var create = await Send(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + scheduled.ToString("O") + "\",\"offset_minutes\":-15}",
            createKey, null);
        Check(create, HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var reminderId = created.RootElement.GetProperty("id").GetString()!;
        if (created.RootElement.GetProperty("version").GetInt64() != 1 ||
            created.RootElement.GetProperty("channel").GetString() != "local")
            throw new InvalidOperationException("Reminder create mismatch.");

        var replay = await Send(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + scheduled.ToString("O") + "\",\"offset_minutes\":-15}",
            createKey, null);
        Check(replay, HttpStatusCode.OK);
        var reused = await Send(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + scheduled.AddHours(1).ToString("O") + "\"}", createKey, null);
        Check(reused, HttpStatusCode.Conflict);
        if (await ErrorCode(reused) != "IDEMPOTENCY_KEY_REUSED")
            throw new InvalidOperationException("Reminder idempotency reuse mismatch.");

        var list = await Send(client, token, HttpMethod.Get, "/tasks/" + taskId + "/reminders", null, null, null);
        Check(list, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Reminder list mismatch.");
        }
        var upcoming = await Send(client, token, HttpMethod.Get,
            "/reminders/upcoming?from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")) +
            "&to=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(2).ToString("O")) + "&limit=10",
            null, null, null);
        Check(upcoming, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await upcoming.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Upcoming reminders mismatch.");
        }
        var foreignUpcoming = await Send(client, foreignToken, HttpMethod.Get,
            "/reminders/upcoming?from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")) +
            "&to=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(2).ToString("O")),
            null, null, null);
        Check(foreignUpcoming, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await foreignUpcoming.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Foreign upcoming reminders leaked.");
        }

        var rescheduled = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/reminders/" + reminderId,
            "{\"expected_version\":1,\"patch\":{\"scheduled_at\":\"" +
            scheduled.AddHours(2).ToString("O") + "\",\"enabled\":false}}", Guid.NewGuid(), null);
        Check(rescheduled, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await rescheduled.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("version").GetInt64() != 2 ||
                document.RootElement.GetProperty("enabled").GetBoolean())
                throw new InvalidOperationException("Reminder reschedule mismatch.");
        }
        var defaultList = await Send(client, token, HttpMethod.Get, "/tasks/" + taskId + "/reminders",
            null, null, null);
        Check(defaultList, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await defaultList.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Disabled reminder was listed by default.");
        }
        var allList = await Send(client, token, HttpMethod.Get,
            "/tasks/" + taskId + "/reminders?include_disabled=1", null, null, null);
        Check(allList, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await allList.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Disabled reminder missing from include_disabled list.");
        }
        var stale = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/reminders/" + reminderId,
            "{\"expected_version\":1,\"patch\":{\"enabled\":true}}", Guid.NewGuid(), null);
        Check(stale, HttpStatusCode.Conflict);
        if (await ErrorCode(stale) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Reminder version conflict mismatch.");

        await AssertStatus(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + scheduled.ToString("O") + "\",\"channel\":\"telegram\"}",
            Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + DateTimeOffset.UtcNow.AddDays(-2).ToString("O") + "\"}",
            Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/tasks/" + taskId + "/reminders",
            "{\"scheduled_at\":\"" + scheduled.ToString("O") + "\",\"occurrence_id\":\"" + Guid.NewGuid() + "\"}",
            Guid.NewGuid(), null, HttpStatusCode.NotFound);
        await AssertStatus(client, token, HttpMethod.Delete, "/tasks/" + taskId + "/reminders/" + reminderId,
            null, Guid.NewGuid(), null, HttpStatusCode.BadRequest);
        Check(await Send(client, foreignToken, HttpMethod.Get, "/tasks/" + taskId + "/reminders",
            null, null, null), HttpStatusCode.NotFound);
        Check(await Send(client, token, HttpMethod.Get, "/reminders/upcoming?limit=0", null, null, null),
            HttpStatusCode.BadRequest);

        var delete = await Send(client, token, HttpMethod.Delete,
            "/tasks/" + taskId + "/reminders/" + reminderId, null, Guid.NewGuid(), "\"v2\"");
        Check(delete, HttpStatusCode.NoContent);
        var afterDelete = await Send(client, token, HttpMethod.Get,
            "/tasks/" + taskId + "/reminders?include_disabled=1", null, null, null);
        Check(afterDelete, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await afterDelete.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Deleted reminder still listed.");
        }
        Console.WriteLine("PASS: reminders create/replay/list/upcoming/reschedule/delete, window and owner isolation");
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

    private sealed class ReminderFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
