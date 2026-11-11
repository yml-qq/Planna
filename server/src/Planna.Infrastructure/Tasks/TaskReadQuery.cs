using Dapper;
using Npgsql;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Tasks;

// Read model only. Task writes stay in the EF Core CreateTaskService.
public sealed class TaskReadQuery(string connectionString) : ITaskReadQuery
{
    private const string ListSql = """
        SELECT id AS "Id", list_id AS "ListId", title AS "Title", description AS "Description",
            status AS "Status", priority AS "Priority", start_at AS "StartAt", due_at AS "DueAt",
            due_local_date AS "DueLocalDate", all_day AS "AllDay", time_zone AS "TimeZone",
            estimate_minutes AS "EstimateMinutes", completed_at AS "CompletedAt",
            version AS "Version", created_at AS "CreatedAt"
        FROM tasks
        WHERE owner_id = @OwnerId AND deleted_at IS NULL
            AND (CAST(@ListId AS uuid) IS NULL OR list_id = CAST(@ListId AS uuid))
            AND (CAST(@Status AS text) IS NULL OR status = CAST(@Status AS text))
            AND (CAST(@BeforeCreatedAt AS timestamptz) IS NULL OR
                 (created_at, id) < (CAST(@BeforeCreatedAt AS timestamptz), CAST(@BeforeId AS uuid)))
        ORDER BY created_at DESC, id DESC
        LIMIT @Take
        """;

    public async Task<TaskReadResult> ListAsync(Guid ownerId, TaskPageCursor? cursor, int limit,
        Guid? listId, string? status, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        if (listId is not null)
        {
            var owned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM task_lists WHERE id = CAST(@ListId AS uuid) " +
                "AND owner_id = @OwnerId AND deleted_at IS NULL)",
                new { OwnerId = ownerId, ListId = listId }, cancellationToken: ct));
            if (!owned) return new(TaskReadStatus.ListNotFound);
        }

        var rows = (await connection.QueryAsync<TaskDbRow>(new CommandDefinition(ListSql, new
        {
            OwnerId = ownerId,
            ListId = listId,
            Status = status,
            BeforeCreatedAt = cursor?.CreatedAt,
            BeforeId = cursor?.Id,
            Take = limit + 1
        }, cancellationToken: ct))).AsList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(ToView).ToList();
        var next = hasMore
            ? new TaskPageCursor(AsInstant(rows[^1].CreatedAt)!.Value, rows[^1].Id).Encode()
            : null;
        return new(TaskReadStatus.Ok, new TaskReadPage(items, next));
    }

    private static TaskReadView ToView(TaskDbRow row) => new(
        row.Id, row.ListId, row.Title, row.Description, row.Status, row.Priority,
        AsInstant(row.StartAt), AsInstant(row.DueAt), row.DueLocalDate, row.AllDay, row.TimeZone,
        row.EstimateMinutes, AsInstant(row.CompletedAt), row.Version);

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
        public DateTime CreatedAt { get; set; }
    }
}
