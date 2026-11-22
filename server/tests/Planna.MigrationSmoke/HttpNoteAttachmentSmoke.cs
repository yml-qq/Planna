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

internal static class HttpNoteAttachmentSmoke
{
    public static async Task RunAsync(NpgsqlConnection db)
    {
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var note = Guid.NewGuid();
        var foreignNote = Guid.NewGuid();
        await Exec(db, "INSERT INTO users(id,email) VALUES($1,'attach-one@example.test'),($2,'attach-two@example.test')", owner, foreign);
        await Exec(db, "INSERT INTO notes(id,owner_id,title,content) VALUES($1,$2,'Attachment note','Body')", note, owner);
        await Exec(db, "INSERT INTO notes(id,owner_id,title,content) VALUES($1,$2,'Foreign note','Body')", foreignNote, foreign);
        var ownerSession = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await AddSession(db, owner, ownerSession);
        await AddSession(db, foreign, foreignSession);

        Environment.SetEnvironmentVariable("JWT_ISSUER", "planna-http-test");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "planna-http-test-client");
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", "local-test-signing-key-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_CODE_PEPPER", "local-test-code-pepper-32-bytes-minimum-2026");
        Environment.SetEnvironmentVariable("AUTH_OUTBOX_KEY", Convert.ToBase64String(new byte[32]));
        await using var factory = new AttachmentFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(owner, ownerSession);
        var foreignToken = issuer.Issue(foreign, foreignSession);

        Check(await Upload(client, null, note, Guid.NewGuid(), "hello.txt", "text/plain", "hello"),
            HttpStatusCode.Unauthorized);
        Check(await Upload(client, token, note, Guid.Empty, "hello.txt", "text/plain", "hello"),
            HttpStatusCode.BadRequest);
        Check(await Upload(client, token, Guid.NewGuid(), Guid.NewGuid(), "hello.txt", "text/plain", "hello"),
            HttpStatusCode.NotFound);
        Check(await Upload(client, foreignToken, note, Guid.NewGuid(), "hello.txt", "text/plain", "hello"),
            HttpStatusCode.NotFound);
        Check(await Upload(client, token, note, Guid.NewGuid(), "hello.exe", "application/octet-stream", "hello"),
            HttpStatusCode.UnsupportedMediaType);
        Check(await Upload(client, token, note, Guid.NewGuid(), "empty.txt", "text/plain", ""),
            HttpStatusCode.UnprocessableEntity);

        var attachmentId = Guid.NewGuid();
        var uploaded = await Upload(client, token, note, attachmentId, "hello.txt", "text/plain", "hello");
        Check(uploaded, HttpStatusCode.Created);
        using (var document = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            if (root.GetProperty("id").GetString() != attachmentId.ToString() ||
                root.GetProperty("filename").GetString() != "hello.txt" ||
                root.GetProperty("content_type").GetString() != "text/plain" ||
                root.GetProperty("size_bytes").GetInt64() != 5)
                throw new InvalidOperationException("Attachment upload mismatch.");
        }

        var replay = await Upload(client, token, note, attachmentId, "hello.txt", "text/plain", "hello");
        Check(replay, HttpStatusCode.OK);
        if (await Scalar(db, "SELECT count(*) FROM note_attachments WHERE note_id=$1 AND deleted_at IS NULL", note) != 1)
            throw new InvalidOperationException("Attachment replay created a duplicate row.");

        var list = await List(client, token, note);
        Check(list, HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            if (document.RootElement.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Attachment list mismatch.");
        }
        Check(await List(client, foreignToken, note), HttpStatusCode.NotFound);
        Check(await List(client, token, foreignNote), HttpStatusCode.NotFound);

        var download = await Download(client, token, note, attachmentId);
        Check(download, HttpStatusCode.OK);
        if (await download.Content.ReadAsStringAsync() != "hello" ||
            download.Content.Headers.ContentType?.MediaType != "text/plain")
            throw new InvalidOperationException("Attachment download mismatch.");
        Check(await Download(client, foreignToken, note, attachmentId), HttpStatusCode.NotFound);
        Check(await Download(client, token, note, Guid.NewGuid()), HttpStatusCode.NotFound);

        Check(await Delete(client, token, note, attachmentId), HttpStatusCode.NoContent);
        Check(await Download(client, token, note, attachmentId), HttpStatusCode.NotFound);
        Check(await Delete(client, token, note, attachmentId), HttpStatusCode.NoContent);
        Check(await Delete(client, token, note, Guid.NewGuid()), HttpStatusCode.NotFound);
        if (await Scalar(db, "SELECT count(*) FROM note_attachments WHERE note_id=$1 AND deleted_at IS NOT NULL", note) != 1)
            throw new InvalidOperationException("Attachment soft delete mismatch.");
        Console.WriteLine("PASS: note attachments scope, upload/list/download, idempotency, type/size and soft delete");
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string? token, Guid noteId,
        Guid attachmentId, string filename, string contentType, string body)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(bytes, "file", filename);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/notes/" + noteId + "/attachments")
        {
            Content = form
        };
        if (attachmentId != Guid.Empty)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", attachmentId.ToString());
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> List(HttpClient client, string? token, Guid noteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/notes/" + noteId + "/attachments");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Download(HttpClient client, string? token, Guid noteId,
        Guid attachmentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/notes/" + noteId + "/attachments/" + attachmentId);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string? token, Guid noteId,
        Guid attachmentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            "/notes/" + noteId + "/attachments/" + attachmentId);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private sealed class AttachmentFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseContentRoot(AppContext.BaseDirectory);
    }
}
