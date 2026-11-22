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

internal static class HttpGoalOrderSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'goal-order@example.test'),($2,'goal-order-other@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        var goal = Guid.NewGuid();
        var foreignGoal = Guid.NewGuid();
        await Exec(db, "INSERT INTO goals(id,owner_id,title) VALUES($1,$2,'Ship')", goal, owner);
        await Exec(db, "INSERT INTO goals(id,owner_id,title) VALUES($1,$2,'Other')", foreignGoal, foreign);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        await Exec(db, "INSERT INTO milestones(id,owner_id,goal_id,title,position) VALUES($1,$2,$3,'Draft',1),($4,$2,$3,'Review',2),($5,$2,$3,'Ship',3)",
            first, owner, goal, second, third);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new OrderFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Reorder(client, null, goal, Guid.NewGuid(), Body(1, third, first, second)),
            HttpStatusCode.Unauthorized);

        var reorderKey = Guid.NewGuid();
        var reorderBody = Body(1, third, first, second);
        var reordered = await Reorder(client, token, goal, reorderKey, reorderBody);
        Check(reordered, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await reordered.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt64() != 2)
                throw new InvalidOperationException("Reorder goal version mismatch.");
            var milestones = root.GetProperty("milestones");
            if (milestones.GetArrayLength() != 3 ||
                milestones[0].GetProperty("id").GetString() != third.ToString() ||
                milestones[1].GetProperty("id").GetString() != first.ToString() ||
                milestones[2].GetProperty("id").GetString() != second.ToString() ||
                milestones[0].GetProperty("position").GetInt32() != 1 ||
                milestones[0].GetProperty("version").GetInt64() != 2)
                throw new InvalidOperationException("Reorder order mismatch.");
        }

        var replay = await Reorder(client, token, goal, reorderKey, reorderBody);
        Check(replay, HttpStatusCode.OK);

        var stale = await Reorder(client, token, goal, Guid.NewGuid(), Body(1, first, second, third));
        Check(stale, HttpStatusCode.Conflict);
        if (await ErrorCode(stale) != "VERSION_CONFLICT")
            throw new InvalidOperationException("Reorder stale version mismatch.");

        await AssertStatus(client, token, goal, Guid.NewGuid(), Body(2, first, first, second),
            HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, goal, Guid.NewGuid(), Body(2, first, second),
            HttpStatusCode.UnprocessableEntity);
        await AssertStatus(client, token, goal, Guid.NewGuid(), Body(2, first, second, Guid.NewGuid()),
            HttpStatusCode.UnprocessableEntity);
        Check(await Reorder(client, foreignToken, goal, Guid.NewGuid(), Body(2, first, second, third)),
            HttpStatusCode.NotFound);
        Check(await Reorder(client, token, goal, Guid.Empty, Body(2, first, second, third)),
            HttpStatusCode.BadRequest);
        Check(await Reorder(client, token, Guid.NewGuid(), Guid.NewGuid(), Body(2, first, second, third)),
            HttpStatusCode.NotFound);
        Console.WriteLine("PASS: goal milestone batch reorder, replay, stale version, invalid sets, owner and idempotency checks");
    }

    private static string Body(long expectedVersion, params Guid[] ids) =>
        "{\"expected_version\":" + expectedVersion + ",\"milestone_ids\":[" +
        string.Join(',', ids.Select(id => "\"" + id + "\"")) + "]}";

    private static async Task<HttpResponseMessage> Reorder(HttpClient client, string? token, Guid goalId,
        Guid mutation, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "/goals/" + goalId + "/milestones/order")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (mutation != Guid.Empty)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", mutation.ToString());
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AssertStatus(HttpClient client, string token, Guid goalId, Guid mutation,
        string body, HttpStatusCode expected)
    {
        var response = await Reorder(client, token, goalId, mutation, body);
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

    private sealed class OrderFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
