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
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Habits;

// Daily habit logs: one row per (habit, local date); PUT is an idempotent upsert that also
// supports explicit backfill. Logs stay out of the sync log in HAB-01B (server-only aggregate).
public sealed class HabitLogService(TaskWriteContext db, TimeProvider clock)
    : IHabitLogUpsert, IHabitLogQuery, IHabitStreakQuery
{
    private static readonly string[] Statuses = ["done", "skipped"];

    public async Task<HabitLogResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId,
        DateOnly logDate, UpsertHabitLogRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || habitId == Guid.Empty ||
            logDate == default)
            return new(HabitLogStatus.Invalid, Code: "INVALID_HABIT_LOG");
        var quantity = request.Quantity ?? 1;
        var status = request.Status ?? "done";
        if (quantity is < 0 or > 1000 || !Statuses.Contains(status))
            return new(HabitLogStatus.Invalid, Code: "INVALID_HABIT_LOG");
        var hash = Hash("PUT /habits/" + habitId + "/logs/" + logDate.ToString("yyyy-MM-dd") + "\n" +
                        JsonSerializer.Serialize(request));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"habitlog:" + ownerId.ToString("N")}, 0))", ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(HabitLogStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var habitExists = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM habits WHERE id={habitId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        if (habitExists != 1) return new(HabitLogStatus.NotFound, Code: "HABIT_NOT_FOUND");

        var now = Truncate(clock.GetUtcNow());
        var existing = await LoadForUpdateAsync(ownerId, habitId, logDate, ct);
        HabitLogView view;
        if (existing is null)
        {
            var id = Guid.NewGuid();
            await ExecuteAsync(db, """
                INSERT INTO habit_logs(id, owner_id, habit_id, log_date, quantity, status, version,
                    created_at, updated_at)
                VALUES(@Id, @Owner, @Habit, @Date, @Quantity, @Status, 1, @Now, @Now)
                """, p =>
            {
                p.Add(new NpgsqlParameter("Id", id));
                p.Add(new NpgsqlParameter("Owner", ownerId));
                p.Add(new NpgsqlParameter("Habit", habitId));
                p.Add(new NpgsqlParameter("Date", NpgsqlDbType.Date) { Value = logDate });
                p.Add(new NpgsqlParameter("Quantity", quantity));
                p.Add(new NpgsqlParameter("Status", status));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            }, ct);
            view = new HabitLogView(id, habitId, logDate, quantity, status, 1, now, now);
        }
        else
        {
            var version = existing.Version + 1;
            await ExecuteAsync(db, """
                UPDATE habit_logs SET quantity=@Quantity, status=@Status, version=@Version, updated_at=@Now
                WHERE id=@Id
                """, p =>
            {
                p.Add(new NpgsqlParameter("Quantity", quantity));
                p.Add(new NpgsqlParameter("Status", status));
                p.Add(new NpgsqlParameter("Version", version));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("Id", existing.Id));
            }, ct);
            view = new HabitLogView(existing.Id, habitId, logDate, quantity, status, version,
                existing.CreatedAt, now);
        }
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(view),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(HabitLogStatus.Upserted, view);
    }

    public async Task<HabitLogResult> ListAsync(Guid ownerId, Guid habitId, DateOnly from, DateOnly to,
        CancellationToken ct)
    {
        if (ownerId == Guid.Empty || habitId == Guid.Empty || from == default || to == default ||
            from > to || to.DayNumber - from.DayNumber > 366)
            return new(HabitLogStatus.Invalid, Code: "INVALID_HABIT_LOG_RANGE");
        var habitExists = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM habits WHERE id={habitId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        if (habitExists != 1) return new(HabitLogStatus.NotFound, Code: "HABIT_NOT_FOUND");
        var rows = await db.Database.GetDbConnection().QueryAsync<LogRow>(new CommandDefinition("""
            SELECT id AS "Id", habit_id AS "HabitId", log_date AS "LogDate", quantity AS "Quantity",
                status AS "Status", version AS "Version", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM habit_logs
            WHERE owner_id=@Owner AND habit_id=@Habit AND log_date BETWEEN @From AND @To
            ORDER BY log_date, id
            """, new { Owner = ownerId, Habit = habitId, From = from, To = to }, cancellationToken: ct));
        return new(HabitLogStatus.Ok, Logs: new HabitLogList(rows.Select(ToView).ToList()));
    }

    public async Task<HabitLogResult> GetStreakAsync(Guid ownerId, Guid habitId, DateOnly through,
        CancellationToken ct)
    {
        if (ownerId == Guid.Empty || habitId == Guid.Empty || through == default)
            return new(HabitLogStatus.Invalid, Code: "INVALID_HABIT_LOG");
        var habit = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<StreakHabitRow>(
            new CommandDefinition("""
                SELECT target_quantity AS "TargetQuantity"
                FROM habits WHERE id=@Id AND owner_id=@Owner AND deleted_at IS NULL
                """, new { Id = habitId, Owner = ownerId }, cancellationToken: ct));
        if (habit is null) return new(HabitLogStatus.NotFound, Code: "HABIT_NOT_FOUND");
        var dates = (await db.Database.GetDbConnection().QueryAsync<DateOnly>(new CommandDefinition("""
            SELECT log_date FROM habit_logs
            WHERE owner_id=@Owner AND habit_id=@Habit AND status='done'
              AND quantity >= @Target AND log_date <= @Through
            ORDER BY log_date
            """, new { Owner = ownerId, Habit = habitId, Target = habit.TargetQuantity, Through = through },
            cancellationToken: ct))).ToHashSet();
        var current = 0;
        var cursor = through;
        while (dates.Contains(cursor))
        {
            current += 1;
            cursor = cursor.AddDays(-1);
        }
        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var date in dates.Order())
        {
            run = previous is not null && date == previous.Value.AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = date;
        }
        return new(HabitLogStatus.Ok, Streak: new HabitStreakView(habitId, through, current, longest,
            habit.TargetQuantity));
    }

    private async Task<LogRow?> LoadForUpdateAsync(Guid ownerId, Guid habitId, DateOnly logDate,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<LogRow>(new CommandDefinition("""
            SELECT id AS "Id", habit_id AS "HabitId", log_date AS "LogDate", quantity AS "Quantity",
                status AS "Status", version AS "Version", created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM habit_logs
            WHERE owner_id=@Owner AND habit_id=@Habit AND log_date=@Date
            FOR UPDATE
            """, new { Owner = ownerId, Habit = habitId, Date = logDate },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<HabitLogResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(HabitLogStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        return new(HabitLogStatus.Replayed, JsonSerializer.Deserialize<HabitLogView>(previous.Response));
    }

    private static HabitLogView ToView(LogRow row) => new(row.Id, row.HabitId, row.LogDate,
        row.Quantity, row.Status, row.Version, Instant(row.CreatedAt), Instant(row.UpdatedAt));

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class StreakHabitRow
    {
        public int TargetQuantity { get; set; }
    }

    private sealed class LogRow
    {
        public Guid Id { get; set; }
        public Guid HabitId { get; set; }
        public DateOnly LogDate { get; set; }
        public int Quantity { get; set; }
        public string Status { get; set; } = "done";
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
