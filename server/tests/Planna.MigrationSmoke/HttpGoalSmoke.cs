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

internal static class HttpGoalSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'goal-one@example.test'),($2,'goal-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new GoalFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Send(client, null, HttpMethod.Get, "/goals", null, null, null), HttpStatusCode.Unauthorized);

        var createKey = Guid.NewGuid();
        var create = await Send(client, token, HttpMethod.Post, "/goals",
            "{\"title\":\"Ship v1\",\"description\":\"Release\",\"target_date\":\"2026-12-31\"}",
            createKey, null);
        Check(create, HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var goalId = created.RootElement.GetProperty("id").GetString()!;
        if (created.RootElement.GetProperty("version").GetInt64() != 1 ||
            created.RootElement.GetProperty("progress").GetProperty("percent").GetInt32() != 0)
            throw new InvalidOperationException("Goal create mismatch.");

        var replay = await Send(client, token, HttpMethod.Post, "/goals",
            "{\"title\":\"Ship v1\",\"description\":\"Release\",\"target_date\":\"2026-12-31\"}",
            createKey, null);
        Check(replay, HttpStatusCode.OK);
        var reused = await Send(client, token, HttpMethod.Post, "/goals", "{\"title\":\"Other\"}",
            createKey, null);
        Check(reused, HttpStatusCode.Conflict);
        if (await ErrorCode(reused) != "IDEMPOTENCY_KEY_REUSED")
            throw new InvalidOperationException("Goal idempotency reuse mismatch.");

        var milestone = await Send(client, token, HttpMethod.Post, "/goals/" + goalId + "/milestones",
            "{\"title\":\"Draft\"}", Guid.NewGuid(), null);
        Check(milestone, HttpStatusCode.Created);
        using var milestoneDocument = JsonDocument.Parse(await milestone.Content.ReadAsStringAsync());
        var milestoneId = milestoneDocument.RootElement.GetProperty("id").GetString()!;
        if (milestoneDocument.RootElement.GetProperty("position").GetInt32() != 1)
            throw new InvalidOperationException("Milestone position mismatch.");
        var second = await Send(client, token, HttpMethod.Post, "/goals/" + goalId + "/milestones",
            "{\"title\":\"Review\",\"due_date\":\"2026-12-01\"}", Guid.NewGuid(), null);
        Check(second, HttpStatusCode.Created);

        var completed = await Send(client, token, HttpMethod.Patch,
            "/goals/" + goalId + "/milestones/" + milestoneId,
            "{\"expected_version\":1,\"patch\":{\"completed\":true}}", Guid.NewGuid(), null);
        Check(completed, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await completed.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("version").GetInt64() != 2 ||
                !document.RootElement.GetProperty("completed").GetBoolean())
                throw new InvalidOperationException("Milestone completion mismatch.");
        }

        var detail = await Send(client, token, HttpMethod.Get, "/goals/" + goalId, null, null, null);
        Check(detail, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var progress = document.RootElement.GetProperty("progress");
            if (progress.GetProperty("completed_milestones").GetInt32() != 1 ||
                progress.GetProperty("total_milestones").GetInt32() != 2 ||
                progress.GetProperty("percent").GetInt32() != 50)
                throw new InvalidOperationException("Goal progress mismatch.");
        }

        var goalPatch = await Send(client, token, HttpMethod.Patch, "/goals/" + goalId,
            "{\"expected_version\":1,\"patch\":{\"status\":\"completed\"}}", Guid.NewGuid(), null);
        Check(goalPatch, HttpStatusCode.OK);
        var staleGoal = await Send(client, token, HttpMethod.Patch, "/goals/" + goalId,
            "{\"expected_version\":1,\"patch\":{\"title\":\"Stale\"}}", Guid.NewGuid(), null);
        Check(staleGoal, HttpStatusCode.Conflict);
        if (await ErrorCode(staleGoal) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Goal version conflict mismatch.");

        var deleteMilestone = await Send(client, token, HttpMethod.Delete,
            "/goals/" + goalId + "/milestones/" + milestoneId, null, Guid.NewGuid(), "\"v2\"");
        Check(deleteMilestone, HttpStatusCode.NoContent);
        var afterDelete = await Send(client, token, HttpMethod.Get, "/goals/" + goalId, null, null, null);
        Check(afterDelete, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await afterDelete.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("progress").GetProperty("total_milestones").GetInt32() != 1)
                throw new InvalidOperationException("Milestone delete progress mismatch.");
        }

        Check(await Send(client, foreignToken, HttpMethod.Get, "/goals/" + goalId, null, null, null),
            HttpStatusCode.NotFound);
        Check(await Send(client, foreignToken, HttpMethod.Post, "/goals/" + goalId + "/milestones",
            "{\"title\":\"Hijack\"}", Guid.NewGuid(), null), HttpStatusCode.NotFound);
        Check(await Send(client, foreignToken, HttpMethod.Post, "/goals", "{\"title\":\"Own\"}",
            Guid.NewGuid(), null), HttpStatusCode.Created);

        await AssertStatus(client, token, HttpMethod.Post, "/goals", "{\"title\":\"  \"}",
            Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/goals/" + goalId + "/milestones",
            "{\"title\":\"\"}", Guid.NewGuid(), null, HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, HttpMethod.Post, "/goals/" + Guid.NewGuid() + "/milestones",
            "{\"title\":\"Lost\"}", Guid.NewGuid(), null, HttpStatusCode.NotFound);
        await AssertStatus(client, token, HttpMethod.Delete, "/goals/" + goalId, null, Guid.NewGuid(),
            "\"v1\"", HttpStatusCode.Conflict);
        var deleteGoal = await Send(client, token, HttpMethod.Delete, "/goals/" + goalId, null,
            Guid.NewGuid(), "\"v2\"");
        Check(deleteGoal, HttpStatusCode.NoContent);
        Check(await Send(client, token, HttpMethod.Get, "/goals/" + goalId, null, null, null),
            HttpStatusCode.NotFound);

        var list = await Send(client, token, HttpMethod.Get, "/goals", null, null, null);
        Check(list, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Deleted goal still listed.");
        }
        Console.WriteLine("PASS: goals and milestones CRUD, progress, idempotency, version and owner isolation");
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

    private sealed class GoalFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
