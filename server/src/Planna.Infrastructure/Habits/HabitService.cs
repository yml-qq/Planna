using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Habits;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Habits;

// Habits are a separate aggregate: server-only in this slice (no sync_changes/revision yet),
// every write is idempotent and version checked.
public sealed class HabitService(TaskWriteContext db, TimeProvider clock)
    : IHabitCreate, IHabitUpdate, IHabitDelete, IHabitQuery
{
    private static readonly string[] ScheduleTypes = ["daily", "weekly", "weekdays"];

    public async Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateHabitRequest request,
        CancellationToken ct)
    {
        if (!ValidCreate(request, out var title, out var scheduleType, out var weekdays,
                out var target, out var unit, out var timeZone))
            return new(HabitStatus.Invalid, Code: "INVALID_HABIT");
        var hash = Hash("POST /habits\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"habit:" + ownerId.ToString("N")}, 0))", ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(HabitStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;

        var id = Guid.NewGuid();
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            INSERT INTO habits(id, owner_id, title, schedule_type, weekdays, target_quantity, unit,
                time_zone, version, created_at, updated_at)
            VALUES(@Id, @Owner, @Title, @Schedule, @Weekdays, @Target, @Unit, @Zone, 1, @Now, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("Schedule", scheduleType));
            p.Add(WeekdaysParameter(weekdays));
            p.Add(new NpgsqlParameter("Target", target));
            p.Add(new NpgsqlParameter("Unit", unit));
            p.Add(new NpgsqlParameter("Zone", timeZone));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var view = new HabitView(id, title, scheduleType, weekdays, target, unit, timeZone, null, 1,
            now, now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(HabitStatus.Created, view);
    }

    public async Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId,
        UpdateHabitRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || habitId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.Patch is null)
            return new(HabitStatus.Invalid, Code: "INVALID_HABIT");
        var hash = Hash("PATCH /habits/" + habitId + "\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"habit:" + ownerId.ToString("N")}, 0))", ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadForUpdateAsync(ownerId, habitId, ct);
        if (row is null) return new(HabitStatus.NotFound, Code: "HABIT_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(HabitStatus.VersionConflict, Details: new VersionConflictDetails(habitId, row.Version, []));

        var patch = request.Patch;
        var title = patch.Title?.Trim() ?? row.Title;
        var scheduleType = patch.ScheduleType ?? row.ScheduleType;
        var weekdays = patch.Weekdays ?? row.Weekdays;
        var target = patch.TargetQuantity ?? row.TargetQuantity;
        var unit = patch.Unit?.Trim() ?? row.Unit;
        var timeZone = patch.TimeZone ?? row.TimeZone;
        var pausedAt = patch.Paused switch
        {
            true => Truncate(clock.GetUtcNow()),
            false => (DateTimeOffset?)null,
            null => row.PausedAt
        };
        if (!ValidMerged(title, scheduleType, ref weekdays, target, unit, timeZone))
            return new(HabitStatus.Invalid, Code: "INVALID_HABIT");
        var now = Truncate(clock.GetUtcNow());
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE habits SET title=@Title, schedule_type=@Schedule, weekdays=@Weekdays,
                target_quantity=@Target, unit=@Unit, time_zone=@Zone, paused_at=@Paused,
                version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("Schedule", scheduleType));
            p.Add(WeekdaysParameter(weekdays));
            p.Add(new NpgsqlParameter("Target", target));
            p.Add(new NpgsqlParameter("Unit", unit));
            p.Add(new NpgsqlParameter("Zone", timeZone));
            p.Add(new NpgsqlParameter("Paused", NpgsqlDbType.TimestampTz)
            {
                Value = pausedAt?.UtcDateTime ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", habitId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var view = new HabitView(habitId, title, scheduleType, weekdays, target, unit, timeZone,
            pausedAt, version, row.CreatedAt, now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(HabitStatus.Updated, view);
    }

    public async Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId,
        long expectedVersion, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || habitId == Guid.Empty ||
            expectedVersion < 1)
            return new(HabitStatus.Invalid, Code: "INVALID_HABIT");
        var hash = Hash("DELETE /habits/" + habitId + "\n" + expectedVersion);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"habit:" + ownerId.ToString("N")}, 0))", ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadForUpdateAsync(ownerId, habitId, ct);
        if (row is null) return new(HabitStatus.NotFound, Code: "HABIT_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(HabitStatus.VersionConflict, Details: new VersionConflictDetails(habitId, row.Version, []));
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            UPDATE habits SET deleted_at=@Now, version=version+1, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", habitId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash,
            new { id = habitId, deleted = true }, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(HabitStatus.Deleted);
    }

    public async Task<HabitResult> ListAsync(Guid ownerId, CancellationToken ct)
    {
        var rows = await db.Database.GetDbConnection().QueryAsync<HabitRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", schedule_type AS "ScheduleType",
                weekdays AS "Weekdays", target_quantity AS "TargetQuantity", unit AS "Unit",
                time_zone AS "TimeZone", paused_at AS "PausedAt", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM habits WHERE owner_id=@Owner AND deleted_at IS NULL
            ORDER BY created_at DESC, id
            """, new { Owner = ownerId }, cancellationToken: ct));
        return new(HabitStatus.Ok, Habits: new HabitList(rows.Select(ToView).ToList()));
    }

    public async Task<HabitResult> GetAsync(Guid ownerId, Guid habitId, CancellationToken ct)
    {
        var row = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<HabitRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", schedule_type AS "ScheduleType",
                weekdays AS "Weekdays", target_quantity AS "TargetQuantity", unit AS "Unit",
                time_zone AS "TimeZone", paused_at AS "PausedAt", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM habits WHERE id=@Id AND owner_id=@Owner AND deleted_at IS NULL
            """, new { Id = habitId, Owner = ownerId }, cancellationToken: ct));
        return row is null
            ? new(HabitStatus.NotFound, Code: "HABIT_NOT_FOUND")
            : new(HabitStatus.Ok, ToView(row));
    }

    private async Task<HabitRow?> LoadForUpdateAsync(Guid ownerId, Guid habitId, CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<HabitRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", schedule_type AS "ScheduleType",
                weekdays AS "Weekdays", target_quantity AS "TargetQuantity", unit AS "Unit",
                time_zone AS "TimeZone", paused_at AS "PausedAt", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM habits WHERE id=@Id AND owner_id=@Owner AND deleted_at IS NULL
            FOR UPDATE
            """, new { Id = habitId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<HabitResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash, CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(HabitStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        return stored.RootElement.TryGetProperty("deleted", out _)
            ? new(HabitStatus.Replayed)
            : new(HabitStatus.Replayed, JsonSerializer.Deserialize<HabitView>(previous.Response));
    }

    private static bool ValidCreate(CreateHabitRequest request, out string title, out string scheduleType,
        out IReadOnlyList<short>? weekdays, out int target, out string unit, out string timeZone)
    {
        title = request.Title?.Trim() ?? "";
        scheduleType = request.ScheduleType ?? "";
        weekdays = request.Weekdays;
        target = request.TargetQuantity ?? 1;
        unit = request.Unit?.Trim() ?? "count";
        timeZone = request.TimeZone ?? "";
        return ValidMerged(title, scheduleType, ref weekdays, target, unit, timeZone);
    }

    private static bool ValidMerged(string title, string scheduleType, ref IReadOnlyList<short>? weekdays,
        int target, string unit, string timeZone)
    {
        if (title.Length is < 1 or > 200 || !ScheduleTypes.Contains(scheduleType) ||
            target is < 1 or > 1000 || unit.Length is < 1 or > 20 ||
            string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 128)
            return false;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
        if (scheduleType == "weekly")
        {
            if (weekdays is null || weekdays.Count is < 1 or > 7)
                return false;
            var distinct = weekdays.Distinct().ToList();
            if (distinct.Any(day => day is < 1 or > 7) || distinct.Count != weekdays.Count)
                return false;
            weekdays = distinct.Order().ToList();
            return true;
        }
        weekdays = null;
        return true;
    }

    private static NpgsqlParameter WeekdaysParameter(IReadOnlyList<short>? weekdays) =>
        new("Weekdays", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
        {
            Value = weekdays is null ? DBNull.Value : weekdays.ToArray()
        };

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

    private static HabitView ToView(HabitRow row) => new(row.Id, row.Title, row.ScheduleType,
        row.Weekdays, row.TargetQuantity, row.Unit, row.TimeZone,
        row.PausedAt is { } paused ? Instant(paused) : null, row.Version, Instant(row.CreatedAt),
        Instant(row.UpdatedAt));

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class HabitRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string ScheduleType { get; set; } = "daily";
        public short[]? Weekdays { get; set; }
        public int TargetQuantity { get; set; } = 1;
        public string Unit { get; set; } = "count";
        public string TimeZone { get; set; } = "";
        public DateTime? PausedAt { get; set; }
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
