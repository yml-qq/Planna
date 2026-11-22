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

internal static class HttpWorkdayOverrideSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'workday-one@example.test'),($2,'workday-two@example.test')", owner, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new CalendarFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await List(client, null, "2026-09-01", "2026-10-31", null), HttpStatusCode.Unauthorized);

        var created = await Upsert(client, token, "2026-10-01",
            "{\"kind\":\"holiday\",\"note\":\"National Day\"}");
        Check(created, HttpStatusCode.Created);
        using (var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("date").GetString() != "2026-10-01" ||
                root.GetProperty("kind").GetString() != "holiday" ||
                root.GetProperty("jurisdiction").GetString() != "CN")
                throw new InvalidOperationException("Workday override create mismatch.");
        }

        var replaced = await Upsert(client, token, "2026-10-01",
            "{\"kind\":\"workday\",\"note\":\"make-up day\"}");
        Check(replaced, HttpStatusCode.OK);
        var us = await Upsert(client, token, "2026-10-01",
            "{\"jurisdiction\":\"US\",\"kind\":\"holiday\"}");
        Check(us, HttpStatusCode.Created);

        var list = await List(client, token, "2026-09-01", "2026-10-31", "CN");
        Check(list, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var items = document.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 1 ||
                items[0].GetProperty("kind").GetString() != "workday")
                throw new InvalidOperationException("Workday override list mismatch.");
        }
        var usList = await List(client, token, "2026-09-01", "2026-10-31", "US");
        Check(usList, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await usList.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Workday override jurisdiction filter mismatch.");
        }
        var foreignList = await List(client, foreignToken, "2026-09-01", "2026-10-31", "CN");
        Check(foreignList, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await foreignList.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Workday override owner isolation failed.");
        }

        Check(await Upsert(client, token, "2026-13-01", "{\"kind\":\"holiday\"}"),
            HttpStatusCode.BadRequest);
        Check(await Upsert(client, token, "2026-10-02", "{\"kind\":\"party\"}"),
            HttpStatusCode.UnprocessableEntity);
        Check(await Upsert(client, token, "2026-10-02",
            "{\"kind\":\"holiday\",\"note\":\"" + new string('a', 201) + "\"}"),
            HttpStatusCode.UnprocessableEntity);
        Check(await List(client, token, "2026-01-01", "2027-06-01", null), HttpStatusCode.BadRequest);
        Check(await List(client, token, "2026-09-01", null, null), HttpStatusCode.BadRequest);

        Check(await Delete(client, token, "2026-10-01", "CN"), HttpStatusCode.NoContent);
        Check(await Delete(client, token, "2026-10-01", "CN"), HttpStatusCode.NotFound);
        var after = await List(client, token, "2026-09-01", "2026-10-31", "CN");
        Check(after, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 0)
                throw new InvalidOperationException("Workday override delete did not remove the row.");
        }
        if (await Scalar(db, "SELECT count(*) FROM workday_overrides WHERE owner_id=$1 AND jurisdiction='CN'", owner) != 0)
            throw new InvalidOperationException("Workday override row still present after delete.");
        Console.WriteLine("PASS: workday overrides isolate owners, default jurisdiction, upsert/delete and validation");
    }

    private static async Task<HttpResponseMessage> List(HttpClient client, string? token, string from,
        string? to, string? jurisdiction)
    {
        var query = "from=" + from;
        if (to is not null) query += "&to=" + to;
        if (jurisdiction is not null) query += "&jurisdiction=" + jurisdiction;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/calendar/workday-overrides?" + query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Upsert(HttpClient client, string? token, string date,
        string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "/calendar/workday-overrides/" + date)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string token, string date,
        string jurisdiction)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            "/calendar/workday-overrides/" + date + "?jurisdiction=" + jurisdiction);
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

    private sealed class CalendarFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
