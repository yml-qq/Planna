using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Planna.Application.Notes;

namespace Planna.Infrastructure.Notes;

// Removes soft-deleted attachment metadata and files older than the retention window.
// Files are deleted before rows; a crash in between can leave a row pointing at a missing
// file (download returns ATTACHMENT_FILE_MISSING), which is safer than an orphan file leak.
public sealed class NoteAttachmentCleanup(string connection, IConfiguration configuration)
    : INoteAttachmentCleanup
{
    public async Task<AttachmentCleanupReport> PurgeDeletedAsync(DateTimeOffset olderThan,
        CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        var rows = (await db.QueryAsync<(Guid Id, string StorageKey)>(
            new CommandDefinition("""
                SELECT id AS "Id", storage_key AS "StorageKey"
                FROM note_attachments
                WHERE deleted_at IS NOT NULL AND deleted_at < @Cutoff
                """, new { Cutoff = olderThan.UtcDateTime }, cancellationToken: ct))).ToList();
        var root = NoteAttachmentStorage.Root(configuration);
        var removedFiles = 0;
        foreach (var row in rows)
        {
            var path = NoteAttachmentStorage.PathFor(root, row.StorageKey);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removedFiles++;
                }
            }
            catch (IOException)
            {
                // Keep the row so a later run can retry; do not fail the whole batch.
                continue;
            }
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM note_attachments WHERE id=@Id", new { row.Id }, cancellationToken: ct));
        }
        return new AttachmentCleanupReport(rows.Count, removedFiles);
    }
}
