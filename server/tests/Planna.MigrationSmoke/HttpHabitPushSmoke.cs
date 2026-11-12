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

internal static class HttpHabitPushSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'habit-push-one@example.test'),($2,'habit-push-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new PushFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);
        var device = Guid.NewGuid().ToString();
        var habit = Guid.NewGuid();
        var logId = Guid.NewGuid();

        Check(await Push(client, null, device, Mutation(Guid.NewGuid(), "habit", habit, "upsert", 0,
            "{\"title\":\"Read\",\"schedule_type\":\"daily\",\"target_quantity\":2,\"unit\":\"pages\",\"time_zone\":\"Asia/Shanghai\"}")),
            HttpStatusCode.Unauthorized);

        // Create habit, replay idempotently, then see it in pull.
        var createKey = Guid.NewGuid();
        var create = await Push(client, token, device, Mutation(createKey, "habit", habit, "upsert", 0,
            "{\"title\":\"Read\",\"schedule_type\":\"daily\",\"target_quantity\":2,\"unit\":\"pages\",\"time_zone\":\"Asia/Shanghai\"}"));
        Check(create, HttpStatusCode.OK);
        var created = await Result(create);
        if (created.Status != "accepted" || created.EntityVersion != 1)
            throw new InvalidOperationException("Habit push create mismatch.");
        var replay = await Push(client, token, device, Mutation(createKey, "habit", habit, "upsert", 0,
            "{\"title\":\"Read\",\"schedule_type\":\"daily\",\"target_quantity\":2,\"unit\":\"pages\",\"time_zone\":\"Asia/Shanghai\"}"));
        Check(replay, HttpStatusCode.OK);
        if ((await Result(replay)).Status != "duplicate")
            throw new InvalidOperationException("Habit push replay mismatch.");

        var pull = await Pull(client, token);
        Check(pull, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await pull.Content.ReadAsStringAsync()))
        {
            var changes = document.RootElement.GetProperty("changes");
            if (!changes.EnumerateArray().Any(change =>
                    change.GetProperty("entity_type").GetString() == "habit" &&
                    change.GetProperty("entity_id").GetString() == habit.ToString() &&
                    change.GetProperty("version").GetInt64() == 1))
                throw new InvalidOperationException("Pulled habit change missing.");
        }

        // Update, stale conflict, log create/update and habit delete.
        var update = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit", habit, "upsert", 1,
            "{\"title\":\"Read more\"}"));
        Check(update, HttpStatusCode.OK);
        if ((await Result(update)).EntityVersion != 2)
            throw new InvalidOperationException("Habit push update mismatch.");
        var stale = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit", habit, "upsert", 1,
            "{\"title\":\"Stale\"}"));
        Check(stale, HttpStatusCode.OK);
        var staleResult = await Result(stale);
        if (staleResult.Status != "conflict" || staleResult.EntityVersion != 2)
            throw new InvalidOperationException("Habit push conflict mismatch.");
        var logCreate = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit_log", logId, "upsert", 0,
            "{\"habit_id\":\"" + habit + "\",\"log_date\":\"2026-11-01\",\"quantity\":2,\"status\":\"done\"}"));
        Check(logCreate, HttpStatusCode.OK);
        if ((await Result(logCreate)).EntityVersion != 1)
            throw new InvalidOperationException("Habit log push create mismatch.");
        var duplicateDay = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit_log", Guid.NewGuid(), "upsert", 0,
            "{\"habit_id\":\"" + habit + "\",\"log_date\":\"2026-11-01\",\"quantity\":1,\"status\":\"done\"}"));
        Check(duplicateDay, HttpStatusCode.OK);
        if ((await Result(duplicateDay)).Status != "conflict")
            throw new InvalidOperationException("Habit log same-day conflict missing.");
        var logUpdate = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit_log", logId, "upsert", 1,
            "{\"habit_id\":\"" + habit + "\",\"log_date\":\"2026-11-01\",\"quantity\":3,\"status\":\"done\"}"));
        Check(logUpdate, HttpStatusCode.OK);
        if ((await Result(logUpdate)).EntityVersion != 2)
            throw new InvalidOperationException("Habit log push update mismatch.");
        var deleted = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit", habit, "delete", 2, "{}"));
        Check(deleted, HttpStatusCode.OK);
        if ((await Result(deleted)).Status != "accepted")
            throw new InvalidOperationException("Habit push delete mismatch.");
        var afterDelete = await Push(client, token, device, Mutation(Guid.NewGuid(), "habit_log", Guid.NewGuid(), "upsert", 0,
            "{\"habit_id\":\"" + habit + "\",\"log_date\":\"2026-11-02\",\"quantity\":1,\"status\":\"done\"}"));
        Check(afterDelete, HttpStatusCode.OK);
        if ((await Result(afterDelete)).Status != "rejected")
            throw new InvalidOperationException("Deleted habit log push was not rejected.");

        // Validation and ownership.
        await AssertRejected(client, token, device, "goal", Guid.NewGuid(), 0, "{}");
        await AssertRejected(client, token, device, "habit", Guid.NewGuid(), 0,
            "{\"schedule_type\":\"daily\",\"time_zone\":\"Asia/Shanghai\"}");
        await AssertRejected(client, token, device, "habit", Guid.NewGuid(), 0,
            "{\"title\":\"Weekly\",\"schedule_type\":\"weekly\",\"time_zone\":\"Asia/Shanghai\"}");
        await AssertRejected(client, token, device, "habit_log", Guid.NewGuid(), 0,
            "{\"habit_id\":\"" + habit + "\",\"log_date\":\"2026-13-01\"}");
        var foreignUpdate = await Push(client, foreignToken, device, Mutation(Guid.NewGuid(), "habit", habit, "upsert", 2,
            "{\"title\":\"Hijack\"}"));
        Check(foreignUpdate, HttpStatusCode.OK);
        if ((await Result(foreignUpdate)).Status != "rejected")
            throw new InvalidOperationException("Foreign habit push was not rejected.");

        // Five accepted mutations, two entity types, one revision each.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='habit'", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1 AND entity_type='habit_log'", owner) != 2 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 5 ||
            await Scalar(db, "SELECT count(*) FROM habits WHERE id=$1 AND deleted_at IS NOT NULL AND version=3", habit) != 1)
            throw new InvalidOperationException("Habit push sync accounting mismatch.");
        Console.WriteLine("PASS: sync push habits and logs, replay, conflict, validation, tombstones, owner isolation");
    }

    private static string Mutation(Guid mutationId, string entityType, Guid entityId, string op,
        long baseVersion, string payload) =>
        "{\"device_id\":\"" + Guid.NewGuid() + "\",\"mutations\":[{\"mutation_id\":\"" + mutationId +
        "\",\"entity_type\":\"" + entityType + "\",\"entity_id\":\"" + entityId + "\",\"op\":\"" + op +
        "\",\"base_version\":" + baseVersion + ",\"payload\":" + payload + "}]}";

    private static async Task<HttpResponseMessage> Push(HttpClient client, string? token, string device,
        string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sync/push")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Pull(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sync/pull?limit=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<(string Status, long? EntityVersion)> Result(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("results")[0];
        return (
            result.GetProperty("status").GetString() ?? "",
            result.TryGetProperty("entity_version", out var version) && version.ValueKind == JsonValueKind.Number
                ? version.GetInt64()
                : null);
    }

    private static async Task AssertRejected(HttpClient client, string token, string device,
        string entityType, Guid entityId, long baseVersion, string payload)
    {
        var response = await Push(client, token, device,
            Mutation(Guid.NewGuid(), entityType, entityId, "upsert", baseVersion, payload));
        Check(response, HttpStatusCode.OK);
        if ((await Result(response)).Status != "rejected")
            throw new InvalidOperationException($"Expected rejected push for {entityType}.");
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

    private sealed class PushFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
