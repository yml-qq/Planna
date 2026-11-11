using Dapper;
using Npgsql;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Tasks;

// Read model only. List writes stay in the EF Core list services.
public sealed class TaskListQuery(string connectionString) : ITaskListReadQuery
{
    private const string Columns = "id AS \"Id\", name AS \"Name\", kind AS \"Kind\", version AS \"Version\"";

    public async Task<IReadOnlyList<TaskListView>> ListAsync(Guid ownerId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<TaskListDbRow>(new CommandDefinition(
            $"SELECT {Columns} FROM task_lists WHERE owner_id = @OwnerId AND deleted_at IS NULL ORDER BY sort_order, id",
            new { OwnerId = ownerId }, cancellationToken: ct));
        return rows.Select(ToView).ToList();
    }

    public async Task<GetTaskListResult> GetAsync(Guid ownerId, Guid listId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TaskListDbRow>(new CommandDefinition(
            $"SELECT {Columns} FROM task_lists WHERE id = @Id AND owner_id = @OwnerId AND deleted_at IS NULL LIMIT 1",
            new { Id = listId, OwnerId = ownerId }, cancellationToken: ct));
        return row is null
            ? new(GetTaskListStatus.NotFound)
            : new(GetTaskListStatus.Ok, ToView(row));
    }

    private static TaskListView ToView(TaskListDbRow row) => new(row.Id, row.Name, row.Kind, row.Version);

    private sealed class TaskListDbRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "custom";
        public long Version { get; set; }
    }
}
