using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Sync;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Sync;

// Notes are owner-scoped aggregates; pushes use the same mutation ledger and sync_changes as tasks/habits.
public sealed partial class SyncPushService
{
    private async Task<SyncPushResult> UpsertNoteAsync(Guid ownerId, SyncMutation mutation,
        CancellationToken ct)
    {
        if (!NotePayload.TryParse(mutation.Payload, out var payload))
            return Rejected(mutation.MutationId);
        var row = await LoadNoteAsync(ownerId, mutation.EntityId, ct);
        if (mutation.BaseVersion == 0)
        {
            if (!payload.HasContent || string.IsNullOrWhiteSpace(payload.Content))
                return Rejected(mutation.MutationId);
            if (row is not null)
                return Conflict(mutation.MutationId, NoteJson(row), row.Version, true);
            var title = (payload.Title ?? "").Trim();
            var content = payload.Content!;
            if (!ValidNote(title, content) ||
                !await NoteTaskOwnedAsync(ownerId, payload.TaskId, ct))
                return Rejected(mutation.MutationId);
            var now = clock.GetUtcNow();
            await ExecuteAsync(db, """
                INSERT INTO notes(id, owner_id, title, content, task_id, version, created_at, updated_at)
                VALUES(@Id, @Owner, @Title, @Content, @Task, 1, @Now, @Now)
                """, p =>
            {
                p.Add(new NpgsqlParameter("Id", mutation.EntityId));
                p.Add(new NpgsqlParameter("Owner", ownerId));
                p.Add(new NpgsqlParameter("Title", title));
                p.Add(new NpgsqlParameter("Content", content));
                p.Add(new NpgsqlParameter("Task", NpgsqlDbType.Uuid)
                {
                    Value = payload.TaskId is { } taskId ? taskId : (object)DBNull.Value
                });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            }, ct);
            var revision = await BumpRevision(ownerId, ct);
            var created = new NoteRow
            {
                Id = mutation.EntityId,
                Title = title,
                Content = content,
                TaskId = payload.TaskId,
                Version = 1,
                CreatedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime
            };
            var view = NoteJson(created);
            db.Changes.Add(Change(ownerId, revision, "note", mutation.EntityId, "upsert", 1, view, now));
            db.Mutations.Add(new MutationRow
            {
                OwnerId = ownerId,
                MutationId = mutation.MutationId,
                RequestHash = Hash(mutation),
                Response = view.GetRawText(),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await transactionCommit(ct);
            return new SyncPushResult(mutation.MutationId, "accepted", 1, view, false);
        }

        if (row is null || row.DeletedAt is not null) return Rejected(mutation.MutationId);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, NoteJson(row), row.Version, true);
        var mergedTitle = payload.HasTitle ? (payload.Title ?? "").Trim() : row.Title;
        var mergedContent = payload.HasContent ? payload.Content! : row.Content;
        var mergedTask = payload.HasTaskId ? payload.TaskId : row.TaskId;
        if (!ValidNote(mergedTitle, mergedContent) ||
            !await NoteTaskOwnedAsync(ownerId, mergedTask, ct))
            return Rejected(mutation.MutationId);
        var newVersion = row.Version + 1;
        var updatedAt = clock.GetUtcNow();
        await ExecuteAsync(db, """
            UPDATE notes SET title=@Title, content=@Content, task_id=@Task, version=@Version,
                updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", mergedTitle));
            p.Add(new NpgsqlParameter("Content", mergedContent));
            p.Add(new NpgsqlParameter("Task", NpgsqlDbType.Uuid)
            {
                Value = mergedTask is { } taskId ? taskId : (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Version", newVersion));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = updatedAt.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", mutation.EntityId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var revision2 = await BumpRevision(ownerId, ct);
        var updated = new NoteRow
        {
            Id = row.Id,
            Title = mergedTitle,
            Content = mergedContent,
            TaskId = mergedTask,
            Version = newVersion,
            CreatedAt = row.CreatedAt,
            UpdatedAt = updatedAt.UtcDateTime
        };
        var view2 = NoteJson(updated);
        db.Changes.Add(Change(ownerId, revision2, "note", mutation.EntityId, "upsert", newVersion, view2, updatedAt));
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutation.MutationId,
            RequestHash = Hash(mutation),
            Response = view2.GetRawText(),
            CreatedAt = updatedAt
        });
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return new SyncPushResult(mutation.MutationId, "accepted", newVersion, view2, false);
    }

    private async Task<SyncPushResult> DeleteNoteAsync(Guid ownerId, SyncMutation mutation,
        CancellationToken ct)
    {
        if (mutation.BaseVersion < 1) return Rejected(mutation.MutationId);
        var row = await LoadNoteAsync(ownerId, mutation.EntityId, ct);
        if (row is null) return Rejected(mutation.MutationId);
        if (row.DeletedAt is not null)
            return new SyncPushResult(mutation.MutationId, "duplicate", row.Version, null, false);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, NoteJson(row), row.Version, true);
        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await ExecuteAsync(db, """
            UPDATE notes SET deleted_at=@Now, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", newVersion));
            p.Add(new NpgsqlParameter("Id", mutation.EntityId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var revision = await BumpRevision(ownerId, ct);
        var tombstone = JsonSerializer.SerializeToElement(new
        {
            id = mutation.EntityId,
            version = newVersion,
            deleted_at = now
        });
        db.Changes.Add(Change(ownerId, revision, "note", mutation.EntityId, "delete", newVersion, tombstone, now));
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutation.MutationId,
            RequestHash = Hash(mutation),
            Response = JsonSerializer.Serialize(tombstone),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return new SyncPushResult(mutation.MutationId, "accepted", newVersion, tombstone, false);
    }

    private async Task<NoteRow?> LoadNoteAsync(Guid ownerId, Guid noteId, CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", content AS "Content", task_id AS "TaskId",
                version AS "Version", created_at AS "CreatedAt", updated_at AS "UpdatedAt",
                deleted_at AS "DeletedAt"
            FROM notes WHERE id=@Id AND owner_id=@Owner
            """, new { Id = noteId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<bool> NoteTaskOwnedAsync(Guid ownerId, Guid? taskId, CancellationToken ct)
    {
        if (taskId is not { } id) return true;
        return await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM tasks WHERE id={id} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct) == 1;
    }

    private static bool ValidNote(string title, string content) =>
        title.Length <= 200 && content.Length <= 20000 &&
        (title.Trim().Length > 0 || content.Trim().Length > 0);

    private static JsonElement NoteJson(NoteRow row) => JsonSerializer.SerializeToElement(new
    {
        id = row.Id,
        title = row.Title,
        content = row.Content,
        task_id = row.TaskId,
        version = row.Version,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    });

    private sealed class NoteRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public Guid? TaskId { get; set; }
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private sealed class NotePayload
    {
        public string? Title { get; set; }
        public bool HasTitle { get; set; }
        public string? Content { get; set; }
        public bool HasContent { get; set; }
        public Guid? TaskId { get; set; }
        public bool HasTaskId { get; set; }

        public static bool TryParse(JsonElement element, out NotePayload payload)
        {
            payload = new NotePayload();
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "title":
                        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            return false;
                        payload.Title = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                        payload.HasTitle = true;
                        break;
                    case "content":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.Content = value.GetString();
                        payload.HasContent = true;
                        break;
                    case "task_id":
                        if (value.ValueKind == JsonValueKind.Null) payload.TaskId = null;
                        else if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var taskId))
                            payload.TaskId = taskId;
                        else return false;
                        payload.HasTaskId = true;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }
}
