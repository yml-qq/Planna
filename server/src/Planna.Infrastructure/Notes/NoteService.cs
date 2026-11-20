using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Notes;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Notes;

// Light notes: Markdown text plus an optional task link. Search is server-side ILIKE over
// title/content (bounded limit); no attachment or collaborative editing in this slice.
public sealed class NoteService(TaskWriteContext db, TimeProvider clock) : INoteService
{
    public async Task<NoteResult> ListAsync(Guid ownerId, string? query, Guid? taskId, int limit,
        CancellationToken ct)
    {
        if (limit is < 1 or > 100) return new(NoteStatus.Invalid, Code: "INVALID_LIMIT");
        var term = (query ?? "").Trim();
        if (term.Length > 100) return new(NoteStatus.Invalid, Code: "INVALID_NOTE");
        var sql = """
            SELECT id AS "Id", title AS "Title", content AS "Content", task_id AS "TaskId",
                version AS "Version", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM notes
            WHERE owner_id=@Owner AND deleted_at IS NULL
                AND (@Pattern = '' OR title ILIKE @Pattern ESCAPE '\' OR content ILIKE @Pattern ESCAPE '\')
            """;
        if (taskId is not null) sql += " AND task_id=@TaskId";
        sql += " ORDER BY updated_at DESC, id DESC LIMIT @Limit";
        var rows = await db.Database.GetDbConnection().QueryAsync<NoteRow>(new CommandDefinition(sql,
            new
            {
                Owner = ownerId,
                TaskId = taskId,
                Pattern = term.Length == 0 ? "" : "%" + EscapeLike(term) + "%",
                Limit = limit
            }, db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(NoteStatus.Ok, Notes: new NoteList(rows.Select(ToView).ToList()));
    }

    public async Task<NoteResult> GetAsync(Guid ownerId, Guid noteId, CancellationToken ct)
    {
        var row = await LoadAsync(ownerId, noteId, forUpdate: false, ct);
        return row is null
            ? new(NoteStatus.NotFound, Code: "NOTE_NOT_FOUND")
            : new(NoteStatus.Ok, ToView(row));
    }

    public async Task<NoteResult> CreateAsync(Guid ownerId, Guid mutationId,
        CreateNoteRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty ||
            !ValidBody(request.Title, request.Content))
            return new(NoteStatus.Invalid, Code: "INVALID_NOTE");
        var hash = Hash("POST /notes\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(NoteStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        if (request.TaskId is { } taskId && !await TaskOwnedAsync(ownerId, taskId, ct))
            return new(NoteStatus.NotFound, Code: "TASK_NOT_FOUND");

        var id = Guid.NewGuid();
        var now = Truncate(clock.GetUtcNow());
        var title = request.Title?.Trim() ?? "";
        var content = request.Content ?? "";
        await ExecuteAsync(db, """
            INSERT INTO notes(id, owner_id, title, content, task_id, version, created_at, updated_at)
            VALUES(@Id, @Owner, @Title, @Content, @Task, 1, @Now, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("Content", content));
            p.Add(new NpgsqlParameter("Task", NpgsqlDbType.Uuid)
            {
                Value = request.TaskId ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var view = new NoteView(id, title, content, request.TaskId, 1, now, now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(NoteStatus.Created, view);
    }

    public async Task<NoteResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid noteId,
        UpdateNoteRequest request, CancellationToken ct)
    {
        if (noteId == Guid.Empty || request.ExpectedVersion < 1 || request.Patch is null)
            return new(NoteStatus.Invalid, Code: "INVALID_NOTE");
        var hash = Hash("PATCH /notes/" + noteId + "\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadAsync(ownerId, noteId, forUpdate: true, ct);
        if (row is null) return new(NoteStatus.NotFound, Code: "NOTE_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(NoteStatus.VersionConflict,
                Details: new VersionConflictDetails(noteId, row.Version, []));
        var title = request.Patch.Title?.Trim() ?? row.Title;
        var content = request.Patch.Content ?? row.Content;
        if (!ValidBody(title, content)) return new(NoteStatus.Invalid, Code: "INVALID_NOTE");
        var now = Truncate(clock.GetUtcNow());
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE notes SET title=@Title, content=@Content, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("Content", content));
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", noteId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var view = new NoteView(noteId, title, content, row.TaskId, version, Instant(row.CreatedAt), now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(NoteStatus.Updated, view);
    }

    public async Task<NoteResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid noteId,
        long expectedVersion, CancellationToken ct)
    {
        if (noteId == Guid.Empty || expectedVersion < 1)
            return new(NoteStatus.Invalid, Code: "INVALID_NOTE");
        var hash = Hash("DELETE /notes/" + noteId + "\n" + expectedVersion);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadAsync(ownerId, noteId, forUpdate: true, ct);
        if (row is null) return new(NoteStatus.NotFound, Code: "NOTE_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(NoteStatus.VersionConflict,
                Details: new VersionConflictDetails(noteId, row.Version, []));
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            UPDATE notes SET deleted_at=@Now, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", row.Version + 1));
            p.Add(new NpgsqlParameter("Id", noteId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, new { deleted = true }, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(NoteStatus.Deleted);
    }

    private async Task<NoteRow?> LoadAsync(Guid ownerId, Guid noteId, bool forUpdate,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", content AS "Content", task_id AS "TaskId",
                version AS "Version", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM notes WHERE id=@Id AND owner_id=@Owner AND deleted_at IS NULL
            """ + (forUpdate ? " FOR UPDATE" : ""),
            new { Id = noteId, Owner = ownerId }, db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: ct));

    private async Task<bool> TaskOwnedAsync(Guid ownerId, Guid taskId, CancellationToken ct) =>
        await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM tasks WHERE id={taskId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct) == 1;

    private async Task<NoteResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(NoteStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        if (stored.RootElement.TryGetProperty("deleted", out _)) return new(NoteStatus.Replayed);
        var view = JsonSerializer.Deserialize<NoteView>(previous.Response);
        return view is null ? new(NoteStatus.Replayed) : new(NoteStatus.Replayed, view);
    }

    private static bool ValidBody(string? title, string? content)
    {
        var trimmedTitle = title?.Trim() ?? "";
        var body = content ?? "";
        return trimmedTitle.Length <= 200 && body.Length <= 20000 &&
               (trimmedTitle.Length > 0 || body.Trim().Length > 0);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static NoteView ToView(NoteRow row) => new(row.Id, row.Title, row.Content, row.TaskId,
        row.Version, Instant(row.CreatedAt), Instant(row.UpdatedAt));

    private static MutationRow Mutation(Guid ownerId, Guid mutationId, string hash, object response,
        DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(response),
            CreatedAt = now
        };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class NoteRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public Guid? TaskId { get; set; }
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
