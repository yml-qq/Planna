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

internal static class HttpGoalBreakdownSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'goal-ai-one@example.test'),($2,'goal-ai-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);
        var goalId = Guid.NewGuid();
        var foreignGoal = Guid.NewGuid();
        await Exec(db, "INSERT INTO goals(id,owner_id,title,description) VALUES($1,$2,'Learn Rust','core language, async, project')", goalId, owner);
        await Exec(db, "INSERT INTO goals(id,owner_id,title,description) VALUES($1,$2,'Foreign goal','x')", foreignGoal, foreign);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("AI_PROVIDER", "rule_based");
        await using (var factory = new BreakdownFactory())
        {
            using var client = factory.CreateClient();
            var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
            var token = issuer.Issue(owner, ownerSession);
            var foreignToken = issuer.Issue(foreign, foreignSession);

            Check(await Preview(client, null, goalId, "{}"), HttpStatusCode.Unauthorized);

            var preview = await Preview(client, token, goalId,
                "{\"guidance\":\"调研,原型,上线\"}");
            Check(preview, HttpStatusCode.OK);
            using (var document = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
            {
                var root = document.RootElement;
                var suggestions = root.GetProperty("suggestions");
                if (suggestions.GetArrayLength() < 2 ||
                    string.IsNullOrWhiteSpace(suggestions[0].GetProperty("title").GetString()) ||
                    root.GetProperty("assumptions").GetArrayLength() == 0 ||
                    root.GetProperty("goal_id").GetString() != goalId.ToString())
                    throw new InvalidOperationException("Goal breakdown preview mismatch.");
            }
            if (await Scalar(db, "SELECT count(*) FROM milestones WHERE owner_id=$1", owner) != 0)
                throw new InvalidOperationException("Goal breakdown preview wrote data.");
            if (await Scalar(db, "SELECT count(*) FROM goals WHERE owner_id=$1", owner) != 1)
                throw new InvalidOperationException("Goal breakdown preview changed goals.");

            Check(await Preview(client, token, foreignGoal, "{}"), HttpStatusCode.NotFound);
            Check(await Preview(client, foreignToken, goalId, "{}"), HttpStatusCode.NotFound);
            Check(await Preview(client, token, Guid.NewGuid(), "{}"), HttpStatusCode.NotFound);
            var oversized = "{\"guidance\":\"" + new string('a', 2001) + "\"}";
            await AssertStatus(client, token, goalId, oversized, HttpStatusCode.UnprocessableEntity);
            await AssertStatus(client, token, goalId, "not-json", HttpStatusCode.BadRequest);
        }

        Environment.SetEnvironmentVariable("AI_PROVIDER", "none");
        await using (var factory = new BreakdownFactory())
        {
            using var client = factory.CreateClient();
            var token = factory.Services.GetRequiredService<IAccessTokenIssuer>()
                .Issue(owner, ownerSession);
            var response = await Preview(client, token, goalId, "{}");
            Check(response, HttpStatusCode.ServiceUnavailable);
            if (await ErrorCode(response) != "AI_UNAVAILABLE")
                throw new InvalidOperationException("Disabled AI provider was not reported.");
        }
        Console.WriteLine("PASS: goal breakdown preview is read-only, owner scoped, validated and reports unavailable AI");
    }

    private static async Task<HttpResponseMessage> Preview(HttpClient client, string? token,
        Guid goalId, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ai/goals/" + goalId + "/breakdowns/preview")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task AssertStatus(HttpClient client, string token, Guid goalId, string body,
        HttpStatusCode expected)
    {
        var response = await Preview(client, token, goalId, body);
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

    private sealed class BreakdownFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
