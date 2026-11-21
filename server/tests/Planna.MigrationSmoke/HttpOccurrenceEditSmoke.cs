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

internal static class HttpOccurrenceEditSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'occ-edit@example.test'),($2,'occ-edit-other@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        var listId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await Exec(db, "INSERT INTO task_lists(id,owner_id,name) VALUES($1,$2,'Inbox')", listId, owner);
        await Exec(db, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Daily standup')",
            taskId, owner, listId);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new EditFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        var rule = await Send(client, token, HttpMethod.Put, "/tasks/" + taskId + "/recurrence",
            "{\"expected_version\":0,\"kind\":\"rrule\",\"rrule\":\"FREQ=DAILY\",\"time_zone\":\"UTC\",\"dtstart_local\":\"2026-11-10T09:00:00\"}",
            Guid.NewGuid(), null, "application/json");
        Check(rule, HttpStatusCode.OK);

        var occurrenceId = Guid.Empty;
        var occurrenceVersion = 0L;
        var listed = await Send(client, token, HttpMethod.Get,
            "/tasks/" + taskId + "/occurrences?from=2026-11-10&to=2026-11-14", null, null, null);
        Check(listed, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()))
        {
            var item = document.RootElement.GetProperty("items")[0];
            occurrenceId = item.GetProperty("id").GetGuid();
            occurrenceVersion = item.GetProperty("version").GetInt64();
            if (occurrenceVersion != 1 || item.GetProperty("status").GetString() != "todo")
                throw new InvalidOperationException("Occurrence materialization mismatch.");
        }

        var skip = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":1,\"patch\":{\"status\":\"skipped\"}}", Guid.NewGuid(), null);
        Check(skip, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await skip.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("status").GetString() != "skipped" ||
                document.RootElement.GetProperty("version").GetInt64() != 2)
                throw new InvalidOperationException("Occurrence skip mismatch.");
        }

        // Completing a skipped occurrence is rejected.
        var completeSkipped = await Send(client, token, HttpMethod.Post, "/tasks/" + taskId + "/complete",
            "{\"expected_version\":1,\"occurrence_id\":\"" + occurrenceId + "\"}", Guid.NewGuid(), null);
        Check(completeSkipped, HttpStatusCode.Conflict);
        if (await ErrorCode(completeSkipped) != "OCCURRENCE_SKIPPED")
            throw new InvalidOperationException("Skipped occurrence completion was not rejected.");

        var unskip = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":2,\"patch\":{\"status\":\"todo\"}}", Guid.NewGuid(), null);
        Check(unskip, HttpStatusCode.OK);
        var rescheduled = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":3,\"patch\":{\"due_at\":\"2026-11-11T15:30:00Z\"}}", Guid.NewGuid(), null);
        Check(rescheduled, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await rescheduled.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("version").GetInt64() != 4 ||
                !document.RootElement.GetProperty("due_at").GetString()!.StartsWith("2026-11-11T15:30"))
                throw new InvalidOperationException("Occurrence reschedule mismatch.");
        }
        var stale = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":3,\"patch\":{\"status\":\"skipped\"}}", Guid.NewGuid(), null);
        Check(stale, HttpStatusCode.Conflict);
        if (await ErrorCode(stale) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Occurrence version conflict mismatch.");

        // Idempotent replay of the skip path is covered by a fresh occurrence.
        var replayKey = Guid.NewGuid();
        var replayBody = "{\"expected_version\":4,\"patch\":{\"status\":\"skipped\"}}";
        var first = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId, replayBody, replayKey, null);
        Check(first, HttpStatusCode.OK);
        var replay = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId, replayBody, replayKey, null);
        Check(replay, HttpStatusCode.OK);
        var reused = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":5,\"patch\":{\"status\":\"todo\"}}", replayKey, null);
        Check(reused, HttpStatusCode.Conflict);
        if (await ErrorCode(reused) != "IDEMPOTENCY_KEY_REUSED")
            throw new InvalidOperationException("Occurrence idempotency reuse mismatch.");

        var unskipAgain = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":5,\"patch\":{\"status\":\"todo\"}}", Guid.NewGuid(), null);
        Check(unskipAgain, HttpStatusCode.OK);
        var complete = await Send(client, token, HttpMethod.Post, "/tasks/" + taskId + "/complete",
            "{\"expected_version\":1,\"occurrence_id\":\"" + occurrenceId + "\"}", Guid.NewGuid(), null);
        Check(complete, HttpStatusCode.OK);
        var completedPatch = await Send(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":7,\"patch\":{\"status\":\"skipped\"}}", Guid.NewGuid(), null);
        Check(completedPatch, HttpStatusCode.Conflict);
        if (await ErrorCode(completedPatch) != "OCCURRENCE_COMPLETED")
            throw new InvalidOperationException("Completed occurrence edit was not rejected.");

        Check(await Send(client, foreignToken, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":7,\"patch\":{\"status\":\"todo\"}}", Guid.NewGuid(), null),
            HttpStatusCode.NotFound);
        await AssertStatus(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":7,\"patch\":{\"status\":\"done\"}}", Guid.NewGuid(), null,
            HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":7,\"patch\":{}}", Guid.NewGuid(), null,
            HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Patch,
            "/tasks/" + taskId + "/occurrences/" + occurrenceId,
            "{\"expected_version\":7,\"patch\":{\"status\":\"todo\"}}", null, null,
            HttpStatusCode.BadRequest);
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 7)
            throw new InvalidOperationException("Occurrence edit revision mismatch.");
        Console.WriteLine("PASS: occurrence skip/unskip/reschedule, done guard, idempotency, version and owner isolation");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string? token, HttpMethod method,
        string path, string? body, Guid? mutation, string? ifMatch, string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType!);
        if (mutation is { } key) request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AssertStatus(HttpClient client, string token, HttpMethod method, string path,
        string? body, Guid? mutation, string? ifMatch, HttpStatusCode expected)
    {
        var response = await Send(client, token, method, path, body, mutation, ifMatch);
        Check(response, expected);
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
    }

    private static async Task<long> Scalar(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
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

    private sealed class EditFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
