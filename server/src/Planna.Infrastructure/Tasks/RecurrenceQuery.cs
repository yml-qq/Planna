using Dapper;
using Npgsql;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Tasks;

// Read + window materialization for occurrences. Derived rows are idempotent and never bump the owner revision.
public sealed class RecurrenceQuery(string connectionString) : IRecurrenceQuery
{
    public async Task<(GetOccurrenceListStatus Status, RecurrenceView? Recurrence)> GetAsync(
        Guid ownerId, Guid taskId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        if (!await TaskExists(connection, ownerId, taskId, ct))
            return (GetOccurrenceListStatus.TaskNotFound, null);
        var row = await connection.QuerySingleOrDefaultAsync<RecurrenceDbRow>(new CommandDefinition(
            "SELECT task_id AS \"TaskId\", kind AS \"Kind\", rrule AS \"Rrule\", time_zone AS \"TimeZone\", " +
            "dtstart_local AS \"DtstartLocal\", version AS \"Version\" FROM recurrences " +
            "WHERE owner_id = @OwnerId AND task_id = @TaskId",
            new { OwnerId = ownerId, TaskId = taskId }, cancellationToken: ct));
        return row is null
            ? (GetOccurrenceListStatus.RecurrenceNotFound, null)
            : (GetOccurrenceListStatus.Ok, new RecurrenceView(row.TaskId, row.Kind, row.Rrule,
                row.TimeZone, row.DtstartLocal, row.Version));
    }

    public async Task<GetOccurrenceListResult> ListOccurrencesAsync(Guid ownerId, Guid taskId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 90)
            return new(GetOccurrenceListStatus.InvalidWindow);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        if (!await TaskExists(connection, ownerId, taskId, ct))
            return new(GetOccurrenceListStatus.TaskNotFound);
        var rule = await connection.QuerySingleOrDefaultAsync<RecurrenceDbRow>(new CommandDefinition(
            "SELECT task_id AS \"TaskId\", kind AS \"Kind\", rrule AS \"Rrule\", time_zone AS \"TimeZone\", " +
            "dtstart_local AS \"DtstartLocal\", version AS \"Version\" FROM recurrences " +
            "WHERE owner_id = @OwnerId AND task_id = @TaskId",
            new { OwnerId = ownerId, TaskId = taskId }, cancellationToken: ct));
        if (rule is null) return new(GetOccurrenceListStatus.RecurrenceNotFound);
        if (!RecurrenceExpander.TryParse(rule.Rrule, out var parsed))
            return new(GetOccurrenceListStatus.RecurrenceNotFound);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZone); }
        catch (TimeZoneNotFoundException) { return new(GetOccurrenceListStatus.RecurrenceNotFound); }
        catch (InvalidTimeZoneException) { return new(GetOccurrenceListStatus.RecurrenceNotFound); }

        var fromLocal = from.ToDateTime(TimeOnly.MinValue);
        var toLocalExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var candidates = RecurrenceExpander.Expand(parsed, rule.DtstartLocal, fromLocal, toLocalExclusive, zone);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            foreach (var local in candidates)
            {
                if (!RecurrenceExpander.TryResolveInstant(zone, local, out var dueAt)) continue;
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO task_occurrences(owner_id, series_task_id, original_local_start, time_zone, due_at)
                    VALUES(@OwnerId, @TaskId, @Local, @TimeZone, @DueAt)
                    ON CONFLICT (series_task_id, original_local_start) DO NOTHING
                    """, new
                {
                    OwnerId = ownerId,
                    TaskId = taskId,
                    Local = local,
                    TimeZone = rule.TimeZone,
                    DueAt = dueAt.UtcDateTime
                }, transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }

        var rows = await connection.QueryAsync<OccurrenceDbRow>(new CommandDefinition("""
            SELECT id AS "Id", series_task_id AS "TaskId", original_local_start AS "OriginalLocalStart",
                time_zone AS "TimeZone", due_at AS "DueAt", status AS "Status",
                completed_at AS "CompletedAt", version AS "Version"
            FROM task_occurrences
            WHERE owner_id = @OwnerId AND series_task_id = @TaskId
                AND original_local_start >= @From AND original_local_start < @To
            ORDER BY original_local_start
            """, new { OwnerId = ownerId, TaskId = taskId, From = fromLocal, To = toLocalExclusive },
            cancellationToken: ct));
        var items = rows.Select(row => new OccurrenceView(row.Id, row.TaskId, row.OriginalLocalStart,
            row.TimeZone, AsInstant(row.DueAt), row.Status, AsInstant(row.CompletedAt), row.Version)).ToList();
        return new(GetOccurrenceListStatus.Ok, items);
    }

    private static async Task<bool> TaskExists(NpgsqlConnection connection, Guid ownerId, Guid taskId,
        CancellationToken ct) =>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM tasks WHERE id = @TaskId AND owner_id = @OwnerId AND deleted_at IS NULL)",
            new { TaskId = taskId, OwnerId = ownerId }, cancellationToken: ct));

    private static DateTimeOffset? AsInstant(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed class RecurrenceDbRow
    {
        public Guid TaskId { get; set; }
        public string Kind { get; set; } = "rrule";
        public string Rrule { get; set; } = "";
        public string TimeZone { get; set; } = "Asia/Shanghai";
        public DateTime DtstartLocal { get; set; }
        public long Version { get; set; }
    }

    private sealed class OccurrenceDbRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public DateTime OriginalLocalStart { get; set; }
        public string TimeZone { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public string Status { get; set; } = "todo";
        public DateTime? CompletedAt { get; set; }
        public long Version { get; set; }
    }
}
