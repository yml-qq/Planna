using System.Data;
using Dapper;
using Npgsql;
using Planna.Application.Sync;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Sync;

// Read-only snapshot at a single committed revision; the returned cursor continues incremental pulls.
public sealed class SyncBootstrapQuery(string connectionString) : ISyncBootstrapQuery
{
    private const string TaskSql = """
        SELECT id AS "Id", list_id AS "ListId", title AS "Title", description AS "Description",
            status AS "Status", priority AS "Priority", start_at AS "StartAt", due_at AS "DueAt",
            due_local_date AS "DueLocalDate", all_day AS "AllDay", time_zone AS "TimeZone",
            estimate_minutes AS "EstimateMinutes", completed_at AS "CompletedAt", version AS "Version"
        FROM tasks
        WHERE owner_id = @OwnerId AND deleted_at IS NULL
        ORDER BY created_at, id
        """;

    private const string ListSql = """
        SELECT id AS "Id", name AS "Name", kind AS "Kind", version AS "Version"
        FROM task_lists
        WHERE owner_id = @OwnerId AND deleted_at IS NULL
        ORDER BY sort_order, id
        """;

    public async Task<SyncBootstrapResponse> BootstrapAsync(Guid ownerId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var revision = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT revision FROM owner_sync_clocks WHERE owner_id = @OwnerId",
            new { OwnerId = ownerId }, transaction, cancellationToken: ct)) ?? 0L;
        var taskRows = await connection.QueryAsync<TaskDbRow>(new CommandDefinition(TaskSql,
            new { OwnerId = ownerId }, transaction, cancellationToken: ct));
        var listRows = await connection.QueryAsync<ListDbRow>(new CommandDefinition(ListSql,
            new { OwnerId = ownerId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);

        var tasks = taskRows.Select(row => new TaskReadView(row.Id, row.ListId, row.Title, row.Description,
            row.Status, row.Priority, AsInstant(row.StartAt), AsInstant(row.DueAt), row.DueLocalDate,
            row.AllDay, row.TimeZone, row.EstimateMinutes, AsInstant(row.CompletedAt), row.Version)).ToList();
        var lists = listRows.Select(row => new TaskListView(row.Id, row.Name, row.Kind, row.Version)).ToList();
        var cursor = new SyncPageCursor(revision, int.MaxValue).Encode();
        return new SyncBootstrapResponse(cursor, null, tasks, lists);
    }

    private static DateTimeOffset? AsInstant(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed class TaskDbRow
    {
        public Guid Id { get; set; }
        public Guid ListId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "todo";
        public short Priority { get; set; }
        public DateTime? StartAt { get; set; }
        public DateTime? DueAt { get; set; }
        public DateOnly? DueLocalDate { get; set; }
        public bool AllDay { get; set; }
        public string TimeZone { get; set; } = "Asia/Shanghai";
        public short? EstimateMinutes { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long Version { get; set; }
    }

    private sealed class ListDbRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "custom";
        public long Version { get; set; }
    }
}
