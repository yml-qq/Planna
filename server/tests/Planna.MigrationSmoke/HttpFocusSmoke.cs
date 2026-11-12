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

internal static class HttpFocusSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'focus-one@example.test'),($2,'focus-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new FocusFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await List(client, null), HttpStatusCode.Unauthorized);

        var createKey = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var create = await Send(client, token, HttpMethod.Post, "/focus-sessions",
            "{\"planned_seconds\":1500,\"started_at\":\"" + startedAt.ToString("O") + "\"}", createKey, null);
        Check(create, HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var sessionId = created.RootElement.GetProperty("id").GetString()!;
        if (created.RootElement.GetProperty("state").GetString() != "active" ||
            created.RootElement.GetProperty("version").GetInt64() != 1)
            throw new InvalidOperationException("Focus create mismatch.");

        var replay = await Send(client, token, HttpMethod.Post, "/focus-sessions",
            "{\"planned_seconds\":1500,\"started_at\":\"" + startedAt.ToString("O") + "\"}", createKey, null);
        Check(replay, HttpStatusCode.OK);
        using (var replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            if (replayed.RootElement.GetProperty("id").GetString() != sessionId)
                throw new InvalidOperationException("Focus replay mismatch.");
        }
        var reused = await Send(client, token, HttpMethod.Post, "/focus-sessions", "{\"planned_seconds\":900}",
            createKey, null);
        Check(reused, HttpStatusCode.Conflict);
        if (await ErrorCode(reused) != "IDEMPOTENCY_KEY_REUSED")
            throw new InvalidOperationException("Focus idempotency reuse mismatch.");

        var openConflict = await Send(client, token, HttpMethod.Post, "/focus-sessions", "{}", Guid.NewGuid(), null);
        Check(openConflict, HttpStatusCode.Conflict);
        if (await ErrorCode(openConflict) != "FOCUS_SESSION_ACTIVE")
            throw new InvalidOperationException("Open focus session conflict missing.");

        var list = await List(client, token);
        Check(list, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Focus list mismatch.");
        }
        Check(await Send(client, token, HttpMethod.Get, "/focus-sessions/" + sessionId, null, null, null),
            HttpStatusCode.OK);
        Check(await Send(client, foreignToken, HttpMethod.Get, "/focus-sessions/" + sessionId, null, null, null),
            HttpStatusCode.NotFound);

        var pause = await Send(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"paused\",\"actual_seconds\":300}", Guid.NewGuid(), "\"v1\"");
        Check(pause, HttpStatusCode.OK);
        using (var paused = JsonDocument.Parse(await pause.Content.ReadAsStringAsync()))
        {
            if (paused.RootElement.GetProperty("state").GetString() != "paused" ||
                paused.RootElement.GetProperty("version").GetInt64() != 2)
                throw new InvalidOperationException("Focus pause mismatch.");
        }
        var resume = await Send(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"active\",\"actual_seconds\":300}", Guid.NewGuid(), "\"v2\"");
        Check(resume, HttpStatusCode.OK);
        var stale = await Send(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"paused\"}", Guid.NewGuid(), "\"v1\"");
        Check(stale, HttpStatusCode.Conflict);
        if (await ErrorCode(stale) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Focus version conflict missing.");
        var complete = await Send(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"completed\",\"actual_seconds\":1500}", Guid.NewGuid(), "\"v3\"");
        Check(complete, HttpStatusCode.OK);
        using (var completed = JsonDocument.Parse(await complete.Content.ReadAsStringAsync()))
        {
            if (completed.RootElement.GetProperty("state").GetString() != "completed" ||
                completed.RootElement.GetProperty("version").GetInt64() != 4 ||
                completed.RootElement.GetProperty("ended_at").ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException("Focus completion mismatch.");
        }
        var finished = await Send(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"active\"}", Guid.NewGuid(), "\"v4\"");
        Check(finished, HttpStatusCode.Conflict);
        if (await ErrorCode(finished) != "FOCUS_SESSION_FINISHED")
            throw new InvalidOperationException("Finished focus transition was not rejected.");

        // A completed session frees the owner to start another one.
        var again = await Send(client, token, HttpMethod.Post, "/focus-sessions", "{}", Guid.NewGuid(), null);
        Check(again, HttpStatusCode.Created);

        await AssertStatus(client, token, HttpMethod.Post, "/focus-sessions",
            "{\"planned_seconds\":30}", Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/focus-sessions",
            "{\"task_id\":\"" + Guid.NewGuid() + "\"}", Guid.NewGuid(), null, HttpStatusCode.NotFound);
        await AssertStatus(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"running\"}", Guid.NewGuid(), "\"v1\"", HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Patch, "/focus-sessions/" + sessionId,
            "{\"state\":\"paused\"}", Guid.NewGuid(), null, HttpStatusCode.BadRequest);
        Check(await List(client, token, "0"), HttpStatusCode.BadRequest);
        Check(await List(client, token, "101"), HttpStatusCode.BadRequest);
        Console.WriteLine("PASS: focus sessions create/list/get/transition, idempotency, open-session guard, version and owner isolation");
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

    private static async Task<HttpResponseMessage> List(HttpClient client, string? token, string? limit = null)
    {
        var path = "/focus-sessions" + (limit is null ? "" : "?limit=" + limit);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
    }

    private static async Task AssertStatus(HttpClient client, string token, HttpMethod method, string path,
        string body, Guid mutation, string? ifMatch, HttpStatusCode expected)
    {
        var response = await Send(client, token, method, path, body, mutation, ifMatch);
        Check(response, expected);
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

    private sealed class FocusFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
