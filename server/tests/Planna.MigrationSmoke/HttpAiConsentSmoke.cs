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
using Planna.Application.Ai;
using Planna.Application.Identity;

internal static class HttpAiConsentSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'ai-consent-one@example.test'),($2,'ai-consent-two@example.test')", owner, foreign);
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
        Environment.SetEnvironmentVariable("AI_PROVIDER", "rule_based");
        await using var factory = new ConsentFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await GetConsent(client, null), HttpStatusCode.Unauthorized);
        Check(await PutConsent(client, null, Guid.NewGuid(), "{\"allowed_scopes\":[]}"), HttpStatusCode.Unauthorized);
        var emptyConsent = await GetConsentView(client, token);
        if (emptyConsent.AllowedScopes.Count != 0 || emptyConsent.UpdatedAt is not null)
            throw new InvalidOperationException("Initial AI consent was not empty.");

        // Scoped parsing requires a matching persisted consent.
        await AssertCode(await Parse(client, token, ["task_drafts"]), HttpStatusCode.Forbidden, "AI_SCOPE_DENIED");

        var key = Guid.NewGuid();
        var body = "{\"allowed_scopes\":[\"task_drafts\",\"task_notes\"]}";
        var stored = await PutConsent(client, token, key, body);
        Check(stored, HttpStatusCode.OK);
        var view = await stored.Content.ReadFromJsonAsync<AiConsentView>()
            ?? throw new InvalidOperationException("AI consent view missing.");
        if (!view.AllowedScopes.SequenceEqual(["task_drafts", "task_notes"]) || view.UpdatedAt is null)
            throw new InvalidOperationException("AI consent projection mismatch.");
        var replay = await PutConsent(client, token, key, body);
        Check(replay, HttpStatusCode.OK);
        if ((await replay.Content.ReadFromJsonAsync<AiConsentView>())?.UpdatedAt != view.UpdatedAt)
            throw new InvalidOperationException("AI consent replay changed updated_at.");
        var fetched = await GetConsentView(client, token);
        if (!fetched.AllowedScopes.SequenceEqual(view.AllowedScopes) || fetched.UpdatedAt != view.UpdatedAt)
            throw new InvalidOperationException("Stored AI consent differs from the response.");
        Check(await PutConsent(client, token, Guid.NewGuid(), body), HttpStatusCode.OK);

        Check(await Parse(client, token, ["task_drafts"]), HttpStatusCode.OK);
        Check(await Parse(client, token, ["task_drafts", "task_notes"]), HttpStatusCode.OK);
        await AssertCode(await Parse(client, token, ["task_drafts", "plan_preview"]),
            HttpStatusCode.Forbidden, "AI_SCOPE_DENIED");
        await AssertCode(await Parse(client, token, ["task_notes"]), HttpStatusCode.Forbidden, "AI_SCOPE_DENIED");
        await AssertCode(await Parse(client, token, ["root"]), HttpStatusCode.Forbidden, "AI_SCOPE_DENIED");
        Check(await Parse(client, token, []), HttpStatusCode.OK);

        await AssertCode(await PutConsent(client, token, Guid.NewGuid(), "{\"allowed_scopes\":[\"root\"]}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_SCOPE");
        await AssertCode(await PutWithoutKey(client, token, "{\"allowed_scopes\":[]}"),
            HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");

        // Revocation clears effective scopes immediately.
        var revoked = await PutConsent(client, token, Guid.NewGuid(), "{\"allowed_scopes\":[]}");
        Check(revoked, HttpStatusCode.OK);
        if ((await revoked.Content.ReadFromJsonAsync<AiConsentView>())?.AllowedScopes.Count != 0)
            throw new InvalidOperationException("AI consent revocation failed.");
        await AssertCode(await Parse(client, token, ["task_drafts"]), HttpStatusCode.Forbidden, "AI_SCOPE_DENIED");

        // Consent is per owner.
        if ((await GetConsentView(client, foreignToken)).AllowedScopes.Count != 0)
            throw new InvalidOperationException("AI consent leaked across owners.");
        await Exec(db, "UPDATE users SET deleted_at=now() WHERE id=$1", foreign);
        await AssertCode(await GetConsent(client, foreignToken), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Consent is not synced task data.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 3 ||
            await Scalar(db, "SELECT count(*) FROM ai_consents WHERE owner_id=$1 AND allowed_scopes=ARRAY[]::text[]", owner) != 1)
            throw new InvalidOperationException("AI consent wrote sync state or lost the revocation.");
        Console.WriteLine("PASS: AI consent storage, idempotent replay, scope intersection, revocation, owner isolation");
    }

    private static async Task<AiConsentView> GetConsentView(HttpClient client, string token)
    {
        var response = await GetConsent(client, token);
        Check(response, HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<AiConsentView>()
            ?? throw new InvalidOperationException("AI consent response missing.");
    }

    private static async Task<HttpResponseMessage> GetConsent(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ai/consent");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutConsent(HttpClient client, string? token, Guid key,
        string json) => await Put(client, token, key.ToString(), json);

    private static async Task<HttpResponseMessage> PutWithoutKey(HttpClient client, string token, string json) =>
        await Put(client, token, null, json);

    private static async Task<HttpResponseMessage> Put(HttpClient client, string? token, string? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/ai/consent")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Parse(HttpClient client, string token, string[] scopes)
    {
        var json = "{\"input\":\"买牛奶\",\"context\":{\"time_zone\":\"Asia/Shanghai\"," +
                   "\"now\":\"2026-11-11T12:00:00Z\"}" +
                   (scopes.Length == 0 ? "" : ",\"allowed_scopes\":[" +
                       string.Join(",", scopes.Select(x => "\"" + x + "\"")) + "]") + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/parse")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private sealed class ConsentFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
