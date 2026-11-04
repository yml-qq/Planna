using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Planna.Api;
using Planna.Application.Tasks;

internal static class HttpCreateTaskSmoke
{
    private const string Issuer = "planna-http-test";
    private const string Audience = "planna-http-test-client";
    private const string Key = "local-test-signing-key-32-bytes-minimum-2026";
    private static readonly Dictionary<Guid, Guid> SessionIds = [];

    public static async Task RunAsync(NpgsqlConnection database)
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var list = Guid.NewGuid();
        await Execute(database, "INSERT INTO users(id,email) VALUES($1,'http-one@example.test'),($2,'http-two@example.test')", owner, other);
        await Execute(database, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, other);
        await Execute(database, "INSERT INTO task_lists(id,owner_id,name) VALUES($1,$2,'HTTP Inbox')", list, owner);
        SessionIds[owner] = Guid.NewGuid();
        SessionIds[other] = Guid.NewGuid();
        await Execute(database, "INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash) VALUES($1,$2,'Test','windows',$3),($4,$5,'Test','windows',$6)",
            SessionIds[owner], owner, Guid.NewGuid().ToString(), SessionIds[other], other, Guid.NewGuid().ToString());

        Environment.SetEnvironmentVariable("JWT_ISSUER", Issuer);
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", Audience);
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", Key);
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var body = new CreateTaskRequest(Guid.NewGuid(), list, "HTTP task", null, 0,
            null, null, null, false, "Asia/Shanghai", null);
        var key = Guid.NewGuid();

        var missingToken = await Send(client, body, key, null);
        AssertStatus(missingToken, HttpStatusCode.Unauthorized);
        await AssertCode(missingToken, "UNAUTHORIZED");
        var wrongSignature = await Send(client, body, key, Token(owner, Key + "wrong"));
        AssertStatus(wrongSignature, HttpStatusCode.Unauthorized);
        await AssertCode(wrongSignature, "UNAUTHORIZED");
        var expired = await Send(client, body, key, Token(owner, Key, DateTime.UtcNow.AddMinutes(-1)));
        AssertStatus(expired, HttpStatusCode.Unauthorized);
        await AssertCode(expired, "UNAUTHORIZED");
        var emptySubject = await Send(client, body, key, Token(Guid.Empty, Key));
        AssertStatus(emptySubject, HttpStatusCode.Unauthorized);
        await AssertCode(emptySubject, "UNAUTHORIZED");

        var missingKey = await Send(client, body, null, Token(owner, Key));
        AssertStatus(missingKey, HttpStatusCode.BadRequest);
        await AssertCode(missingKey, "INVALID_IDEMPOTENCY_KEY");
        var foreign = await Send(client, body with { Id = Guid.NewGuid() }, Guid.NewGuid(), Token(other, Key));
        AssertStatus(foreign, HttpStatusCode.NotFound);
        await AssertCode(foreign, "LIST_NOT_FOUND");

        var created = await Send(client, body, key, Token(owner, Key));
        AssertStatus(created, HttpStatusCode.Created);
        if (created.Headers.Location?.ToString() != $"/tasks/{body.Id}")
            throw new InvalidOperationException("Created location differs from contract.");
        var createdBody = await created.Content.ReadFromJsonAsync<TaskResponse>();
        if (createdBody?.Id != body.Id || createdBody.Title != body.Title || createdBody.Version != 1)
            throw new InvalidOperationException("Created task body differs from contract.");

        var replay = await Send(client, body, key, Token(owner, Key));
        AssertStatus(replay, HttpStatusCode.Created);
        if (await replay.Content.ReadFromJsonAsync<TaskResponse>() != createdBody)
            throw new InvalidOperationException("HTTP replay changed the task response.");
        var changed = await Send(client, body with { Title = "Changed" }, key, Token(owner, Key));
        AssertStatus(changed, HttpStatusCode.Conflict);
        await AssertCode(changed, "IDEMPOTENCY_KEY_REUSED");

        await Execute(database, "UPDATE users SET deleted_at=now() WHERE id=$1", owner);
        var deleted = await Send(client, body with { Id = Guid.NewGuid() }, Guid.NewGuid(), Token(owner, Key));
        AssertStatus(deleted, HttpStatusCode.Unauthorized);
        await AssertCode(deleted, "ACCOUNT_DELETED");

        if (await Count(database, "SELECT count(*) FROM tasks WHERE owner_id=$1 AND id=$2", owner, body.Id) != 1 ||
            await Count(database, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 1)
            throw new InvalidOperationException("HTTP retries or rejected requests changed database state.");
        Console.WriteLine("PASS: HTTP JWT, status/error contract, owner isolation, replay, deleted account");
    }

    private static string Token(Guid owner, string key, DateTime? expires = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([
                new Claim("sub", owner.ToString()),
                new Claim("sid", SessionIds.GetValueOrDefault(owner).ToString())
            ]),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, CreateTaskRequest body, Guid? key, string? token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/tasks")
        {
            Content = JsonContent.Create(body)
        };
        if (key is not null) message.Headers.Add("Idempotency-Key", key.Value.ToString());
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(message);
    }

    private static void AssertStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private static async Task AssertCode(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (json.RootElement.GetProperty("error").GetProperty("code").GetString() != expected ||
            !json.RootElement.TryGetProperty("request_id", out _))
            throw new InvalidOperationException($"Expected error code {expected}.");
    }

    private static async Task Execute(NpgsqlConnection database, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, database);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection database, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, database);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private sealed class ApiFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
