using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planna.Api;
using Planna.Application.Identity;

internal static class HttpDeviceSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = await User(db, "http-one@example.test");
        var foreignOwner = await User(db, "http-two@example.test");
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        var raceTarget = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var actorRefresh = await AddSession(db, owner, actor, "Actor");
        var targetRefresh = await AddSession(db, owner, target, "Target");
        await AddSession(db, owner, raceTarget, "Race");
        await AddSession(db, foreignOwner, foreign, "Foreign");

        await using var factory = new DeviceFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var actorToken = issuer.Issue(owner, actor);
        var targetToken = issuer.Issue(owner, target);
        Check(await Get(client, null, null), HttpStatusCode.Unauthorized);
        Check(await Get(client, actorToken, "invalid"), HttpStatusCode.BadRequest);
        Check(await Get(client, actorToken, null, "0"), HttpStatusCode.BadRequest);

        var seen = new List<DeviceSessionView>();
        string? cursor = null;
        do
        {
            var response = await Get(client, actorToken, cursor, "1");
            Check(response, HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<DeviceSessionPage>()
                ?? throw new InvalidOperationException("Device page missing.");
            if (page.Devices.Count != 1) throw new InvalidOperationException("Unexpected device page size.");
            seen.AddRange(page.Devices);
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (seen.Count != seen.Select(x => x.Id).Distinct().Count() ||
            !seen.Any(x => x.Id == actor && x.IsCurrent && x.Version == 1) ||
            !seen.Any(x => x.Id == target && !x.IsCurrent) ||
            seen.Any(x => x.Id == foreign) || seen.Any(x => x.Version < 1))
            throw new InvalidOperationException("Dapper device pagination, current flag or owner isolation failed.");

        Check(await Revoke(client, actorToken, foreign, 1, Guid.NewGuid()), HttpStatusCode.NotFound);
        Check(await Revoke(client, actorToken, target, null, Guid.NewGuid()), HttpStatusCode.BadRequest);
        Check(await Revoke(client, actorToken, target, 1, null), HttpStatusCode.BadRequest);
        Check(await Revoke(client, actorToken, target, 2, Guid.NewGuid()), HttpStatusCode.Conflict);
        var key = Guid.NewGuid();
        Check(await Revoke(client, actorToken, target, 1, key), HttpStatusCode.NoContent);
        Check(await Revoke(client, actorToken, target, 1, key), HttpStatusCode.NoContent);
        Check(await Revoke(client, actorToken, target, 1, Guid.NewGuid()), HttpStatusCode.Conflict);
        Check(await Get(client, targetToken, null), HttpStatusCode.Unauthorized);
        Check(await Refresh(client, targetRefresh), HttpStatusCode.Unauthorized);

        var concurrent = await Task.WhenAll(
            Revoke(client, actorToken, raceTarget, 1, Guid.NewGuid()),
            Revoke(client, actorToken, raceTarget, 1, Guid.NewGuid()));
        if (concurrent.Count(x => x.StatusCode == HttpStatusCode.NoContent) != 1 ||
            concurrent.Count(x => x.StatusCode == HttpStatusCode.Conflict) != 1)
            throw new InvalidOperationException("Concurrent revoke was not serialized.");

        var active = await Get(client, actorToken, null);
        Check(active, HttpStatusCode.OK);
        var activePage = await active.Content.ReadFromJsonAsync<DeviceSessionPage>()
            ?? throw new InvalidOperationException("Active device page missing.");
        if (activePage.Devices.Any(x => x.Id == target || x.Id == raceTarget))
            throw new InvalidOperationException("Revoked devices still listed.");

        var logoutKey = Guid.NewGuid();
        Check(await Logout(client, actorToken, 1, logoutKey), HttpStatusCode.NoContent);
        Check(await Get(client, actorToken, null), HttpStatusCode.Unauthorized);
        Check(await Refresh(client, actorRefresh), HttpStatusCode.Unauthorized);
        Console.WriteLine("PASS: device cursor pagination, owner isolation, version/replay/concurrency, logout and token revocation");
    }

    private static async Task<Guid> User(NpgsqlConnection db, string email)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM users WHERE email=$1", db);
        command.Parameters.AddWithValue(email);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing test user."));
    }

    private static async Task<string> AddSession(NpgsqlConnection db, Guid owner, Guid id, string name)
    {
        var refresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refresh)));
        await using var command = new NpgsqlCommand("""
            INSERT INTO device_sessions(id,user_id,device_name,platform,refresh_token_hash)
            VALUES($1,$2,$3,'windows',$4);
            INSERT INTO refresh_token_history(token_hash,session_id) VALUES($4,$1)
            """, db);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(hash);
        await command.ExecuteNonQueryAsync();
        return refresh;
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string? token,
        string? cursor, string? limit = null)
    {
        var query = "/auth/devices" + (limit is null ? "" : "?limit=" + limit);
        if (cursor is not null) query += (limit is null ? "?" : "&") + "cursor=" + Uri.EscapeDataString(cursor);
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Revoke(HttpClient client, string token, Guid id,
        int? version, Guid? key) => await Post(client, $"/auth/devices/{id}/revoke", token, version, key);

    private static async Task<HttpResponseMessage> Logout(HttpClient client, string token,
        int version, Guid key) => await Post(client, "/auth/logout", token, version, key);

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string token,
        int? version, Guid? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (key is not null) request.Headers.Add("Idempotency-Key", key.Value.ToString());
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string token) =>
        client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(token));

    private static void Check(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}.");
    }

    private sealed class DeviceFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
