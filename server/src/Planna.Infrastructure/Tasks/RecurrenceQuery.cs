using Dapper;
using Npgsql;
using NpgsqlTypes;
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
            "SELECT task_id AS \"TaskId\", kind AS \"Kind\", rrule AS \"Rrule\", " +
            "interval_days AS \"IntervalDays\", time_zone AS \"TimeZone\", " +
            "dtstart_local AS \"DtstartLocal\", version AS \"Version\" FROM recurrences " +
            "WHERE owner_id = @OwnerId AND task_id = @TaskId",
            new { OwnerId = ownerId, TaskId = taskId }, cancellationToken: ct));
        return row is null
            ? (GetOccurrenceListStatus.RecurrenceNotFound, null)
            : (GetOccurrenceListStatus.Ok, new RecurrenceView(row.TaskId, row.Kind, row.Rrule,
                row.IntervalDays, row.TimeZone, row.DtstartLocal, row.Version));
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
            "SELECT task_id AS \"TaskId\", kind AS \"Kind\", rrule AS \"Rrule\", " +
            "interval_days AS \"IntervalDays\", time_zone AS \"TimeZone\", " +
            "dtstart_local AS \"DtstartLocal\", version AS \"Version\" FROM recurrences " +
            "WHERE owner_id = @OwnerId AND task_id = @TaskId",
            new { OwnerId = ownerId, TaskId = taskId }, cancellationToken: ct));
        if (rule is null) return new(GetOccurrenceListStatus.RecurrenceNotFound);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZone); }
        catch (TimeZoneNotFoundException) { return new(GetOccurrenceListStatus.RecurrenceNotFound); }
        catch (InvalidTimeZoneException) { return new(GetOccurrenceListStatus.RecurrenceNotFound); }

        var fromLocal = from.ToDateTime(TimeOnly.MinValue);
        var toLocalExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (rule.Kind == "rrule")
        {
            if (!RecurrenceExpander.TryParse(rule.Rrule ?? "", out var parsed))
                return new(GetOccurrenceListStatus.RecurrenceNotFound);
            var candidates = RecurrenceExpander.Expand(parsed, rule.DtstartLocal, fromLocal, toLocalExclusive, zone);
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                foreach (var local in candidates)
                {
                    if (!RecurrenceExpander.TryResolveInstant(zone, local, out var dueAt)) continue;
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO task_occurrences(owner_id, series_task_id, original_local_start, time_zone, due_at)
                        VALUES(@owner, @task, @local, @tz, @due)
                        ON CONFLICT (series_task_id, original_local_start) DO NOTHING
                        """;
                    insert.Parameters.Add(new NpgsqlParameter("owner", ownerId));
                    insert.Parameters.Add(new NpgsqlParameter("task", taskId));
                    insert.Parameters.Add(new NpgsqlParameter("local", NpgsqlDbType.Timestamp) { Value = local });
                    insert.Parameters.Add(new NpgsqlParameter("tz", rule.TimeZone));
                    insert.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.TimestampTz) { Value = dueAt.UtcDateTime });
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
            }
        }

        var items = new List<OccurrenceView>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, series_task_id, original_local_start, time_zone, due_at, status, completed_at, version
                FROM task_occurrences
                WHERE owner_id = @owner AND series_task_id = @task
                    AND original_local_start >= @from AND original_local_start < @to
                ORDER BY original_local_start
                """;
            select.Parameters.Add(new NpgsqlParameter("owner", ownerId));
            select.Parameters.Add(new NpgsqlParameter("task", taskId));
            select.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Timestamp) { Value = fromLocal });
            select.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Timestamp) { Value = toLocalExclusive });
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var dueAt = await reader.IsDBNullAsync(4, ct) ? (DateTime?)null : reader.GetDateTime(4);
                var completedAt = await reader.IsDBNullAsync(6, ct) ? (DateTime?)null : reader.GetDateTime(6);
                items.Add(new OccurrenceView(reader.GetGuid(0), reader.GetGuid(1), reader.GetDateTime(2),
                    reader.GetString(3), AsInstant(dueAt), reader.GetString(5), AsInstant(completedAt),
                    reader.GetInt64(7)));
            }
        }
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
        public string? Rrule { get; set; }
        public int? IntervalDays { get; set; }
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
