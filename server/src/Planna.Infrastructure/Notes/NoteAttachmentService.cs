using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Planna.Application.Notes;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Notes;

// Files are stored outside PostgreSQL under NOTE_ATTACHMENT_DIR; metadata is owner/note scoped.
// Only allowlisted content types and a 5 MiB cap are accepted; filenames are display-only and
// storage paths are built from UUIDs, never from client input.
public sealed class NoteAttachmentService(TaskWriteContext db, TimeProvider clock,
    IConfiguration configuration) : INoteAttachmentService
{
    private readonly long maxBytes =
        ParseLong(configuration["NOTE_ATTACHMENT_MAX_BYTES"], 5L * 1024 * 1024,
            1, 100L * 1024 * 1024);
    private readonly int maxPerNote =
        ParseInt(configuration["NOTE_ATTACHMENT_MAX_PER_NOTE"], 20, 1, 1000);
    private readonly long maxOwnerBytes =
        ParseLong(configuration["NOTE_ATTACHMENT_MAX_OWNER_BYTES"], 200L * 1024 * 1024,
            1, 10L * 1024 * 1024 * 1024);

    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["text/plain"] = ".txt",
        ["text/markdown"] = ".md",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["application/pdf"] = ".pdf"
    };

    private readonly string root = configuration["NOTE_ATTACHMENT_DIR"] ??
        Path.Combine(Path.GetTempPath(), "planna-note-attachments");

    public async Task<NoteAttachmentResult> ListAsync(Guid ownerId, Guid noteId,
        CancellationToken ct)
    {
        if (!await NoteOwnedAsync(ownerId, noteId, ct))
            return new(NoteAttachmentStatus.NotFound, Code: "NOTE_NOT_FOUND");
        var rows = await db.Database.GetDbConnection().QueryAsync<AttachmentRow>(
            new CommandDefinition("""
                SELECT id AS "Id", note_id AS "NoteId", filename AS "Filename",
                    content_type AS "ContentType", size_bytes AS "SizeBytes",
                    created_at AS "CreatedAt"
                FROM note_attachments
                WHERE owner_id=@Owner AND note_id=@Note AND deleted_at IS NULL
                ORDER BY created_at, id
                """, new { Owner = ownerId, Note = noteId },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(NoteAttachmentStatus.Ok,
            List: new NoteAttachmentList(rows.Select(ToView).ToList()));
    }

    public async Task<NoteAttachmentResult> UploadAsync(Guid ownerId, Guid noteId,
        Guid attachmentId, string? filename, string? contentType, long length, Stream content,
        CancellationToken ct)
    {
        var displayName = (filename ?? "").Trim();
        var type = (contentType ?? "").Trim().ToLowerInvariant();
        if (displayName.Length is < 1 or > 255 || displayName.Any(char.IsControl) ||
            !Extensions.ContainsKey(type))
            return new(NoteAttachmentStatus.UnsupportedType, Code: "UNSUPPORTED_ATTACHMENT_TYPE");
        if (length < 1) return new(NoteAttachmentStatus.Invalid, Code: "ATTACHMENT_EMPTY");
        if (length > maxBytes)
            return new(NoteAttachmentStatus.TooLarge, Code: "ATTACHMENT_TOO_LARGE");
        if (!await NoteOwnedAsync(ownerId, noteId, ct))
            return new(NoteAttachmentStatus.NotFound, Code: "NOTE_NOT_FOUND");

        // Serialize quota checks and inserts per owner so concurrent uploads cannot overshoot.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ownerKey = ownerId.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({ownerKey}))", ct);
        var existing = await LoadAsync(ownerId, noteId, attachmentId, ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return new(NoteAttachmentStatus.Replayed, ToView(existing));
        }
        var usage = await UsageAsync(ownerId, noteId, ct);
        if (usage.NoteCount >= maxPerNote)
            return new(NoteAttachmentStatus.Invalid, Code: "ATTACHMENT_NOTE_QUOTA");
        if (usage.OwnerBytes + length > maxOwnerBytes)
            return new(NoteAttachmentStatus.Invalid, Code: "ATTACHMENT_OWNER_QUOTA");

        var extension = Extensions[type];
        var storageKey = $"{ownerId}/{noteId}/{attachmentId}{extension}";
        var path = NoteAttachmentStorage.PathWithinRoot(root, storageKey)
            ?? throw new InvalidOperationException("attachment path escaped the root");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var output = File.Create(path))
                await content.CopyToAsync(output, ct);
            var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO note_attachments(id, owner_id, note_id, filename, content_type,
                    size_bytes, storage_key, version, created_at)
                VALUES({attachmentId}, {ownerId}, {noteId}, {displayName}, {type}, {length},
                    {storageKey}, 1, {now.UtcDateTime})
                """, ct);
            await transaction.CommitAsync(ct);
            return new(NoteAttachmentStatus.Created, new NoteAttachmentView(attachmentId, noteId,
                displayName, type, length, now));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public async Task<(NoteAttachmentResult Result, NoteAttachmentDownload? Download)> OpenAsync(
        Guid ownerId, Guid noteId, Guid attachmentId, CancellationToken ct)
    {
        if (!await NoteOwnedAsync(ownerId, noteId, ct))
            return (new(NoteAttachmentStatus.NotFound, Code: "NOTE_NOT_FOUND"), null);
        var row = await LoadAsync(ownerId, noteId, attachmentId, ct);
        if (row is null) return (new(NoteAttachmentStatus.NotFound, Code: "ATTACHMENT_NOT_FOUND"), null);
        var path = NoteAttachmentStorage.PathWithinRoot(root, row.StorageKey);
        if (path is null || !File.Exists(path))
            return (new(NoteAttachmentStatus.NotFound, Code: "ATTACHMENT_FILE_MISSING"), null);
        return (new(NoteAttachmentStatus.Ok),
            new NoteAttachmentDownload(ToView(row), File.OpenRead(path)));
    }

    public async Task<NoteAttachmentResult> DeleteAsync(Guid ownerId, Guid noteId,
        Guid attachmentId, CancellationToken ct)
    {
        if (!await NoteOwnedAsync(ownerId, noteId, ct))
            return new(NoteAttachmentStatus.NotFound, Code: "NOTE_NOT_FOUND");
        var now = clock.GetUtcNow();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE note_attachments SET deleted_at={now.UtcDateTime},
                version=version+1
            WHERE id={attachmentId} AND owner_id={ownerId} AND note_id={noteId}
                AND deleted_at IS NULL
            """, ct);
        if (affected == 1) return new(NoteAttachmentStatus.Ok);
        var exists = await LoadAsync(ownerId, noteId, attachmentId, ct, includeDeleted: true);
        return exists is null
            ? new(NoteAttachmentStatus.NotFound, Code: "ATTACHMENT_NOT_FOUND")
            : new(NoteAttachmentStatus.Ok);
    }

    private async Task<(long NoteCount, long OwnerBytes)> UsageAsync(Guid ownerId, Guid noteId,
        CancellationToken ct)
    {
        var noteCount = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM note_attachments WHERE owner_id={ownerId} AND note_id={noteId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        var ownerBytes = await db.Database.SqlQuery<long>(
            $"SELECT coalesce(sum(size_bytes),0)::bigint AS \"Value\" FROM note_attachments WHERE owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        return (noteCount, ownerBytes);
    }

    private async Task<bool> NoteOwnedAsync(Guid ownerId, Guid noteId, CancellationToken ct) =>
        await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM notes WHERE id={noteId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct) == 1;

    private async Task<AttachmentRow?> LoadAsync(Guid ownerId, Guid noteId, Guid attachmentId,
        CancellationToken ct, bool includeDeleted = false)
    {
        var deletedFilter = includeDeleted ? "" : " AND deleted_at IS NULL";
        return await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<AttachmentRow>(
            new CommandDefinition($"""
                SELECT id AS "Id", note_id AS "NoteId", filename AS "Filename",
                    content_type AS "ContentType", size_bytes AS "SizeBytes",
                    storage_key AS "StorageKey", created_at AS "CreatedAt"
                FROM note_attachments
                WHERE id=@Id AND owner_id=@Owner AND note_id=@Note{deletedFilter}
                """, new { Id = attachmentId, Owner = ownerId, Note = noteId },
                db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        return int.TryParse(raw, out var value) && value >= min && value <= max ? value : fallback;
    }

    private static long ParseLong(string? raw, long fallback, long min, long max)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        return long.TryParse(raw, out var value) && value >= min && value <= max ? value : fallback;
    }

    private static NoteAttachmentView ToView(AttachmentRow row) => new(row.Id, row.NoteId,
        row.Filename, row.ContentType, row.SizeBytes, Instant(row.CreatedAt));

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class AttachmentRow
    {
        public Guid Id { get; set; }
        public Guid NoteId { get; set; }
        public string Filename { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }
        public string StorageKey { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
