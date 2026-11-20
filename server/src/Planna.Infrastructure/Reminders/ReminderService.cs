using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Reminders;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Reminders;

// Reminders are explicit schedules: clients compute scheduled_at from the task/occurrence due
// time plus offset; the server validates ownership/window and never infers wall-clock focus.
public sealed class ReminderService(TaskWriteContext db, TimeProvider clock) : IReminderService
{
    private static readonly string[] Channels = ["local", "push", "in_app"];

    public async Task<ReminderResult> ListAsync(Guid ownerId, Guid taskId, bool includeDisabled,
        CancellationToken ct)
    {
        if (taskId == Guid.Empty) return new(ReminderStatus.Invalid, Code: "INVALID_TASK_ID");
        if (!await TaskOwnedAsync(ownerId, taskId, ct))
            return new(ReminderStatus.NotFound, Code: "TASK_NOT_FOUND");
        var rows = await db.Database.GetDbConnection().QueryAsync<ReminderRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", occurrence_id AS "OccurrenceId",
                channel AS "Channel", scheduled_at AS "ScheduledAt", offset_minutes AS "OffsetMinutes",
                enabled AS "Enabled", version AS "Version"
            FROM reminders
            WHERE owner_id=@Owner AND task_id=@Task AND deleted_at IS NULL
                AND (@IncludeDisabled OR enabled)
            ORDER BY scheduled_at, id
            """, new { Owner = ownerId, Task = taskId, IncludeDisabled = includeDisabled },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(ReminderStatus.Ok,
            Reminders: new ReminderList(rows.Select(ToView).ToList()));
    }

    public async Task<ReminderResult> UpcomingAsync(Guid ownerId, DateTimeOffset from,
        DateTimeOffset to, int limit, CancellationToken ct)
    {
        if (to < from || to - from > TimeSpan.FromDays(31))
            return new(ReminderStatus.Invalid, Code: "INVALID_REMINDER_WINDOW");
        if (limit is < 1 or > 200) return new(ReminderStatus.Invalid, Code: "INVALID_LIMIT");
        var rows = await db.Database.GetDbConnection().QueryAsync<ReminderRow>(new CommandDefinition("""
            SELECT r.id AS "Id", r.task_id AS "TaskId", r.occurrence_id AS "OccurrenceId",
                r.channel AS "Channel", r.scheduled_at AS "ScheduledAt",
                r.offset_minutes AS "OffsetMinutes", r.enabled AS "Enabled", r.version AS "Version"
            FROM reminders r
            JOIN tasks t ON t.owner_id=r.owner_id AND t.id=r.task_id AND t.deleted_at IS NULL
            WHERE r.owner_id=@Owner AND r.enabled AND r.deleted_at IS NULL
                AND r.scheduled_at>=@From AND r.scheduled_at<@To
            ORDER BY r.scheduled_at, r.id LIMIT @Limit
            """, new
        {
            Owner = ownerId,
            From = from.UtcDateTime,
            To = to.UtcDateTime,
            Limit = limit
        }, db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(ReminderStatus.Ok,
            Reminders: new ReminderList(rows.Select(ToView).ToList()));
    }

    public async Task<ReminderResult> CreateAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateReminderRequest request, CancellationToken ct)
    {
        if (taskId == Guid.Empty || request.ScheduledAt is null ||
            !ValidOffset(request.OffsetMinutes) ||
            !ValidChannel(request.Channel, out var channel) ||
            !ValidSchedule(request.ScheduledAt.Value, clock.GetUtcNow()))
            return new(ReminderStatus.Invalid, Code: "INVALID_REMINDER");
        var hash = Hash("POST /tasks/" + taskId + "/reminders\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(ReminderStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        if (!await TaskOwnedAsync(ownerId, taskId, ct))
            return new(ReminderStatus.NotFound, Code: "TASK_NOT_FOUND");
        var occurrenceId = request.OccurrenceId;
        if (occurrenceId is { } occurrence && occurrence != Guid.Empty)
        {
            if (!await OccurrenceOwnedAsync(ownerId, taskId, occurrence, ct))
                return new(ReminderStatus.NotFound, Code: "OCCURRENCE_NOT_FOUND");
        }
        else
        {
            occurrenceId = null;
        }
        var id = Guid.NewGuid();
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            INSERT INTO reminders(id, owner_id, task_id, occurrence_id, channel, scheduled_at,
                offset_minutes, enabled, version)
            VALUES(@Id, @Owner, @Task, @Occurrence, @Channel, @Scheduled, @Offset, true, 1)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Task", taskId));
            p.Add(new NpgsqlParameter("Occurrence", NpgsqlDbType.Uuid)
            {
                Value = occurrenceId ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Channel", channel));
            p.Add(new NpgsqlParameter("Scheduled", NpgsqlDbType.TimestampTz)
            {
                Value = request.ScheduledAt.Value.UtcDateTime
            });
            p.Add(new NpgsqlParameter("Offset", NpgsqlDbType.Integer)
            {
                Value = request.OffsetMinutes ?? (object)DBNull.Value
            });
        }, ct);
        var view = new ReminderView(id, taskId, occurrenceId, channel, request.ScheduledAt.Value,
            request.OffsetMinutes, true, 1);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(ReminderStatus.Created, view);
    }

    public async Task<ReminderResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid taskId,
        Guid reminderId, UpdateReminderRequest request, CancellationToken ct)
    {
        if (taskId == Guid.Empty || reminderId == Guid.Empty || request.ExpectedVersion < 1 ||
            request.Patch is null)
            return new(ReminderStatus.Invalid, Code: "INVALID_REMINDER");
        var hash = Hash("PATCH /tasks/" + taskId + "/reminders/" + reminderId + "\n" +
                        JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadAsync(ownerId, taskId, reminderId, ct);
        if (row is null) return new(ReminderStatus.NotFound, Code: "REMINDER_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(ReminderStatus.VersionConflict,
                Details: new VersionConflictDetails(reminderId, row.Version, []));
        var patch = request.Patch;
        var scheduledAt = patch.ScheduledAt ?? Instant(row.ScheduledAt);
        var offset = patch.OffsetMinutes ?? row.OffsetMinutes;
        var channel = patch.Channel ?? row.Channel;
        var enabled = patch.Enabled ?? row.Enabled;
        if (!ValidOffset(offset) || !ValidChannel(channel, out channel) ||
            !ValidSchedule(scheduledAt, clock.GetUtcNow()))
            return new(ReminderStatus.Invalid, Code: "INVALID_REMINDER");
        var now = Truncate(clock.GetUtcNow());
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE reminders SET scheduled_at=@Scheduled, offset_minutes=@Offset, channel=@Channel,
                enabled=@Enabled, version=@Version
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Scheduled", NpgsqlDbType.TimestampTz)
            {
                Value = scheduledAt.UtcDateTime
            });
            p.Add(new NpgsqlParameter("Offset", NpgsqlDbType.Integer)
            {
                Value = offset ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Channel", channel));
            p.Add(new NpgsqlParameter("Enabled", enabled));
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Id", reminderId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var view = new ReminderView(reminderId, taskId, row.OccurrenceId, channel, scheduledAt,
            offset, enabled, version);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(ReminderStatus.Updated, view);
    }

    public async Task<ReminderResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        Guid reminderId, long expectedVersion, CancellationToken ct)
    {
        if (taskId == Guid.Empty || reminderId == Guid.Empty || expectedVersion < 1)
            return new(ReminderStatus.Invalid, Code: "INVALID_REMINDER");
        var hash = Hash("DELETE /tasks/" + taskId + "/reminders/" + reminderId + "\n" + expectedVersion);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadAsync(ownerId, taskId, reminderId, ct);
        if (row is null) return new(ReminderStatus.NotFound, Code: "REMINDER_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(ReminderStatus.VersionConflict,
                Details: new VersionConflictDetails(reminderId, row.Version, []));
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            UPDATE reminders SET deleted_at=@Now, version=@Version, enabled=false
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", row.Version + 1));
            p.Add(new NpgsqlParameter("Id", reminderId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, new { deleted = true }, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(ReminderStatus.Deleted);
    }

    private async Task<ReminderRow?> LoadAsync(Guid ownerId, Guid taskId, Guid reminderId,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<ReminderRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", occurrence_id AS "OccurrenceId",
                channel AS "Channel", scheduled_at AS "ScheduledAt", offset_minutes AS "OffsetMinutes",
                enabled AS "Enabled", version AS "Version"
            FROM reminders
            WHERE id=@Id AND task_id=@Task AND owner_id=@Owner AND deleted_at IS NULL
            FOR UPDATE
            """, new { Id = reminderId, Task = taskId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<bool> TaskOwnedAsync(Guid ownerId, Guid taskId, CancellationToken ct) =>
        await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM tasks WHERE id={taskId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct) == 1;

    private async Task<bool> OccurrenceOwnedAsync(Guid ownerId, Guid taskId, Guid occurrenceId,
        CancellationToken ct) =>
        await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM task_occurrences WHERE id={occurrenceId} AND owner_id={ownerId} AND series_task_id={taskId}")
            .SingleAsync(ct) == 1;

    private async Task<ReminderResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(ReminderStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        if (stored.RootElement.TryGetProperty("deleted", out _)) return new(ReminderStatus.Replayed);
        var view = JsonSerializer.Deserialize<ReminderView>(previous.Response);
        return view is null ? new(ReminderStatus.Replayed) : new(ReminderStatus.Replayed, view);
    }

    private async Task LockAsync(Guid ownerId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"reminder:" + ownerId.ToString("N")}, 0))", ct);

    private static bool ValidOffset(int? offset) => offset is null or >= -10080 and <= 10080;

    private static bool ValidChannel(string? channel, out string normalized)
    {
        normalized = (channel ?? "local").Trim().ToLowerInvariant();
        return Channels.Contains(normalized);
    }

    private static bool ValidSchedule(DateTimeOffset scheduledAt, DateTimeOffset now) =>
        scheduledAt >= now.AddDays(-1) && scheduledAt <= now.AddDays(366);

    private static ReminderView ToView(ReminderRow row) => new(row.Id, row.TaskId, row.OccurrenceId,
        row.Channel, Instant(row.ScheduledAt), row.OffsetMinutes, row.Enabled, row.Version);

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

    private sealed class ReminderRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public Guid? OccurrenceId { get; set; }
        public string Channel { get; set; } = "local";
        public DateTime ScheduledAt { get; set; }
        public int? OffsetMinutes { get; set; }
        public bool Enabled { get; set; }
        public long Version { get; set; }
    }
}
