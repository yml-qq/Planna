using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Focus;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Focus;

// Focus sessions are a server-only aggregate in this slice; every write is idempotent and
// version checked. started_at/ended_at/actual_seconds are client supplied (see F07): the server
// never infers focus time from wall clock.
public sealed class FocusSessionService(TaskWriteContext db, TimeProvider clock) : IFocusSessionService
{
    private static readonly string[] States = ["active", "paused", "completed", "abandoned"];
    private static readonly string[] TerminalStates = ["completed", "abandoned"];
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        ["active"] = ["paused", "completed", "abandoned"],
        ["paused"] = ["active", "completed", "abandoned"]
    };

    public async Task<FocusResult> CreateAsync(Guid ownerId, Guid mutationId,
        CreateFocusSessionRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var planned = request.PlannedSeconds ?? 1500;
        if (planned is < 60 or > 86400)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var now = Truncate(clock.GetUtcNow());
        var startedAt = request.StartedAt is { } started ? Truncate(started) : now;
        if (startedAt < now.AddDays(-7) || startedAt > now.AddMinutes(5))
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var hash = Hash("POST /focus-sessions\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"focus:" + ownerId.ToString("N")}, 0))", ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(FocusStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;

        if (request.TaskId is { } taskId && taskId != Guid.Empty)
        {
            var owned = await db.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM tasks WHERE id={taskId} AND owner_id={ownerId} AND deleted_at IS NULL")
                .SingleAsync(ct);
            if (owned != 1) return new(FocusStatus.NotFound, Code: "TASK_NOT_FOUND");
        }
        else
        {
            request = request with { TaskId = null };
        }

        var open = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM focus_sessions WHERE owner_id={ownerId} AND state IN ('active','paused')")
            .SingleAsync(ct);
        if (open != 0) return new(FocusStatus.Conflict, Code: "FOCUS_SESSION_ACTIVE");

        var id = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO focus_sessions(id, owner_id, task_id, started_at, planned_seconds,
                actual_seconds, break_seconds, state, version, created_at, updated_at)
            VALUES(@Id, @Owner, @Task, @Started, @Planned, 0, 0, 'active', 1, @Now, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Task", NpgsqlDbType.Uuid)
            {
                Value = request.TaskId ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Started", NpgsqlDbType.TimestampTz) { Value = startedAt.UtcDateTime });
            p.Add(new NpgsqlParameter("Planned", planned));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var view = new FocusSessionView(id, request.TaskId, startedAt, null, planned, 0, 0, "active", 1,
            now, now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(FocusStatus.Created, view);
    }

    public async Task<FocusResult> ListAsync(Guid ownerId, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) return new(FocusStatus.Invalid, Code: "INVALID_LIMIT");
        var rows = await db.Database.GetDbConnection().QueryAsync<FocusRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", started_at AS "StartedAt", ended_at AS "EndedAt",
                planned_seconds AS "PlannedSeconds", actual_seconds AS "ActualSeconds",
                break_seconds AS "BreakSeconds", state AS "State", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM focus_sessions WHERE owner_id=@Owner
            ORDER BY started_at DESC, id DESC LIMIT @Limit
            """, new { Owner = ownerId, Limit = limit },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return new(FocusStatus.Ok,
            Sessions: new FocusSessionList(rows.Select(ToView).ToList()));
    }

    public async Task<FocusResult> GetAsync(Guid ownerId, Guid sessionId, CancellationToken ct)
    {
        var row = await LoadAsync(ownerId, sessionId, forUpdate: false, ct);
        return row is null
            ? new(FocusStatus.NotFound, Code: "FOCUS_SESSION_NOT_FOUND")
            : new(FocusStatus.Ok, ToView(row));
    }

    public async Task<FocusResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid sessionId,
        long expectedVersion, UpdateFocusSessionRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || sessionId == Guid.Empty ||
            expectedVersion < 1 || request.State is null)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var requestedState = request.State.Trim().ToLowerInvariant();
        if (!States.Contains(requestedState))
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var actual = request.ActualSeconds ?? 0;
        var breakSeconds = request.BreakSeconds ?? 0;
        if (actual is < 0 or > 86400 || breakSeconds is < 0 or > 86400)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var hash = Hash("PATCH /focus-sessions/" + sessionId + "\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"focus:" + ownerId.ToString("N")}, 0))", ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var row = await LoadAsync(ownerId, sessionId, forUpdate: true, ct);
        if (row is null) return new(FocusStatus.NotFound, Code: "FOCUS_SESSION_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(FocusStatus.VersionConflict,
                Details: new VersionConflictDetails(sessionId, row.Version, []));
        if (TerminalStates.Contains(row.State))
            return new(FocusStatus.Conflict, Code: "FOCUS_SESSION_FINISHED");
        if (!Transitions.TryGetValue(row.State, out var allowed) || !allowed.Contains(requestedState))
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_TRANSITION");

        var now = Truncate(clock.GetUtcNow());
        var terminal = TerminalStates.Contains(requestedState);
        DateTimeOffset? endedAt = terminal ? Truncate(request.EndedAt ?? now) : null;
        if (terminal && request.EndedAt is not null && endedAt < Instant(row.StartedAt))
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        if (!terminal && request.EndedAt is not null)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_SESSION");
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE focus_sessions SET state=@State, actual_seconds=@Actual, break_seconds=@Break,
                ended_at=@Ended, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("State", requestedState));
            p.Add(new NpgsqlParameter("Actual", actual));
            p.Add(new NpgsqlParameter("Break", breakSeconds));
            p.Add(new NpgsqlParameter("Ended", NpgsqlDbType.TimestampTz)
            {
                Value = endedAt?.UtcDateTime ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", sessionId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var view = new FocusSessionView(row.Id, row.TaskId, Instant(row.StartedAt), endedAt, row.PlannedSeconds,
            actual, breakSeconds, requestedState, version, Instant(row.CreatedAt), now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(FocusStatus.Updated, view);
    }

    public async Task<FocusResult> StatsAsync(Guid ownerId, DateOnly from, DateOnly to,
        string timeZone, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 92)
            return new(FocusStatus.Invalid, Code: "INVALID_FOCUS_RANGE");
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return new(FocusStatus.Invalid, Code: "INVALID_TIME_ZONE");
        }
        catch (InvalidTimeZoneException)
        {
            return new(FocusStatus.Invalid, Code: "INVALID_TIME_ZONE");
        }
        var startLocal = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, zone);
        var rows = await db.Database.GetDbConnection().QueryAsync<FocusDayRow>(new CommandDefinition("""
            SELECT (started_at AT TIME ZONE @Zone)::date AS "Date",
                count(*) AS "Total",
                count(*) FILTER (WHERE state='completed') AS "Completed",
                count(*) FILTER (WHERE state='abandoned') AS "Abandoned",
                coalesce(sum(actual_seconds) FILTER (WHERE state IN ('completed','abandoned')),0)
                    AS "ActualSeconds"
            FROM focus_sessions
            WHERE owner_id=@Owner AND started_at>=@Start AND started_at<@End
            GROUP BY 1 ORDER BY 1
            """, new { Owner = ownerId, Zone = timeZone, Start = startUtc, End = endUtc },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        var days = rows
            .Select(row => new FocusDayStat(row.Date.ToString("yyyy-MM-dd"), row.Completed,
                row.ActualSeconds))
            .ToList();
        var stats = new FocusStats(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), timeZone,
            days.Sum(day => day.CompletedSessions), days.Sum(day => day.CompletedSessions),
            rows.Sum(row => row.Abandoned), days.Sum(day => day.ActualSeconds), days);
        var withAbandoned = stats with
        {
            TotalSessions = rows.Sum(row => row.Total)
        };
        return new(FocusStatus.Ok, Stats: withAbandoned);
    }

    private async Task<FocusRow?> LoadAsync(Guid ownerId, Guid sessionId, bool forUpdate,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<FocusRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", started_at AS "StartedAt", ended_at AS "EndedAt",
                planned_seconds AS "PlannedSeconds", actual_seconds AS "ActualSeconds",
                break_seconds AS "BreakSeconds", state AS "State", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM focus_sessions WHERE id=@Id AND owner_id=@Owner
            """ + (forUpdate ? " FOR UPDATE" : ""),
            new { Id = sessionId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<FocusResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(FocusStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        return new(FocusStatus.Replayed, JsonSerializer.Deserialize<FocusSessionView>(previous.Response));
    }

    private static FocusSessionView ToView(FocusRow row) => new(row.Id, row.TaskId,
        Instant(row.StartedAt), row.EndedAt is { } ended ? Instant(ended) : null, row.PlannedSeconds,
        row.ActualSeconds, row.BreakSeconds, row.State, row.Version, Instant(row.CreatedAt),
        Instant(row.UpdatedAt));

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

    private sealed class FocusDayRow
    {
        public DateTime Date { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Abandoned { get; set; }
        public long ActualSeconds { get; set; }
    }

    private sealed class FocusRow
    {
        public Guid Id { get; set; }
        public Guid? TaskId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int PlannedSeconds { get; set; }
        public int ActualSeconds { get; set; }
        public int BreakSeconds { get; set; }
        public string State { get; set; } = "active";
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
