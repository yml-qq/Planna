using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;
using Planna.Application.Privacy;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;

internal static class HttpDeletionSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        // Start from a clean step-up queue so an earlier smoke's pending challenge cannot be dispatched here.
        await Exec(db, "DELETE FROM auth_step_up_email_outbox");
        await Exec(db, "DELETE FROM auth_step_up_challenges");
        var owner = Guid.NewGuid();
        var email = "deletion-smoke@example.test";
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,$2)", owner, email);
        await Exec(db, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1)", owner);
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        await AddSession(db, owner, firstSession);
        await AddSession(db, owner, secondSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        var sender = new CaptureSender();
        await using var factory = new DeletionFactory(sender);
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, firstSession);
        var secondToken = issuer.Issue(owner, secondSession);

        Check(await Delete(client, null, Guid.NewGuid(), "{\"confirmation_token\":\"x\",\"acknowledge_offline_devices\":true}"),
            HttpStatusCode.Unauthorized);
        Check(await Delete(client, token, null, "{\"confirmation_token\":\"x\",\"acknowledge_offline_devices\":true}"),
            HttpStatusCode.BadRequest);
        await AssertCode(await Delete(client, token, Guid.NewGuid(),
            "{\"confirmation_token\":\"placeholder-token-value-1234\",\"acknowledge_offline_devices\":true}"),
            HttpStatusCode.Forbidden, "STEP_UP_REQUIRED");

        // Obtain a real step-up token through the email challenge queue.
        var challenge = await Challenge(client, token);
        Check(challenge, HttpStatusCode.Accepted);
        if (!await Dispatch(factory, sender) || sender.Codes.Count != 1)
            throw new InvalidOperationException("Deletion step-up code was not sent.");
        var verified = await Verify(client, token, sender.Codes[0]);
        Check(verified, HttpStatusCode.OK);
        var stepUp = await verified.Content.ReadFromJsonAsync<StepUpVerifyResponse>()
            ?? throw new InvalidOperationException("Deletion step-up token missing.");

        await AssertCode(await Delete(client, token, Guid.NewGuid(),
            "{\"confirmation_token\":\"" + stepUp.ConfirmationToken + "\",\"acknowledge_offline_devices\":false}"),
            HttpStatusCode.UnprocessableEntity, "INVALID_DELETION_REQUEST");

        var deletionKey = Guid.NewGuid();
        var deletionBody = "{\"confirmation_token\":\"" + stepUp.ConfirmationToken +
                           "\",\"acknowledge_offline_devices\":true}";
        var accepted = await Delete(client, token, deletionKey, deletionBody);
        Check(accepted, HttpStatusCode.Accepted);
        var view = await accepted.Content.ReadFromJsonAsync<DeleteAccountResponse>()
            ?? throw new InvalidOperationException("Deletion response missing.");
        if (view.Status != "accepted" || view.PolicyVersion != "account-deletion-v1" ||
            view.DeletionRequestId == Guid.Empty || view.RequestedAt > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Deletion response projection mismatch.");

        // Account, sessions and deletion request are committed atomically.
        if (await Scalar(db, "SELECT count(*) FROM users WHERE id=$1 AND deleted_at IS NOT NULL", owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM device_sessions WHERE user_id=$1 AND revoked_at IS NOT NULL", owner) != 2 ||
            await Scalar(db, "SELECT count(*) FROM account_deletion_requests WHERE id=$1 AND owner_id=$2 AND status='accepted' AND policy_version='account-deletion-v1'", view.DeletionRequestId, owner) != 1 ||
            await Scalar(db, "SELECT count(*) FROM client_mutations WHERE owner_id=$1", owner) != 1)
            throw new InvalidOperationException("Deletion did not commit account, sessions and request atomically.");

        // Old devices can no longer authenticate, sync or delete; every path returns ACCOUNT_DELETED.
        await AssertCode(await Get(client, token, "/me/exports"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");
        await AssertCode(await Get(client, secondToken, "/sync/pull?limit=10"), HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");
        await AssertCode(await Delete(client, token, Guid.NewGuid(), deletionBody),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");
        await AssertCode(await Delete(client, secondToken, Guid.NewGuid(), deletionBody),
            HttpStatusCode.Unauthorized, "ACCOUNT_DELETED");

        // Deletion is a terminal privacy action, never part of the sync log.
        if (await Scalar(db, "SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 0 ||
            await Scalar(db, "SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 0)
            throw new InvalidOperationException("Deletion wrote business sync state.");
        Console.WriteLine("PASS: account deletion with step-up token, atomic session revocation, ACCOUNT_DELETED on old devices");
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string? token, Guid? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/me/deletion-requests")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Challenge(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/step-up/challenges");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Verify(HttpClient client, string token, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/step-up")
        {
            Content = new StringContent("{\"purpose\":\"account_delete\",\"code\":\"" + code + "\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<bool> Dispatch(DeletionFactory factory, ILoginCodeSender sender)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = new StepUpEmailDispatcher(
            scope.ServiceProvider.GetRequiredService<AuthContext>(), sender,
            scope.ServiceProvider.GetRequiredService<LoginCodeCipher>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            NullLogger<StepUpEmailDispatcher>.Instance);
        return await dispatcher.DispatchOneAsync(CancellationToken.None);
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

    private sealed class CaptureSender : ILoginCodeSender
    {
        public List<string> Codes { get; } = [];

        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            Codes.Add(code);
            return Task.CompletedTask;
        }
    }

    private sealed class DeletionFactory(ILoginCodeSender sender) : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton(sender));
        }
    }
}
