using Dapper;
using Npgsql;
using Planna.Application.Tags;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Tasks;

// Read model only. Tag assignment writes stay in SetTaskTagsService.
public sealed class TaskTagQuery(string connectionString) : ITaskTagQuery
{
    public async Task<GetTaskTagsResult> GetAsync(Guid ownerId, Guid taskId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM tasks WHERE id = @TaskId AND owner_id = @OwnerId AND deleted_at IS NULL)",
            new { TaskId = taskId, OwnerId = ownerId }, cancellationToken: ct));
        if (!exists) return new(GetTaskTagsStatus.TaskNotFound);
        var rows = await connection.QueryAsync<TagDbRow>(new CommandDefinition("""
            SELECT t.id AS "Id", t.name AS "Name", t.color AS "Color", t.version AS "Version"
            FROM task_tags tt
            JOIN tags t ON t.owner_id = tt.owner_id AND t.id = tt.tag_id
            WHERE tt.owner_id = @OwnerId AND tt.task_id = @TaskId AND t.deleted_at IS NULL
            ORDER BY lower(t.name), t.id
            """, new { TaskId = taskId, OwnerId = ownerId }, cancellationToken: ct));
        return new(GetTaskTagsStatus.Ok, rows.Select(x => new TagView(x.Id, x.Name, x.Color, x.Version)).ToList());
    }

    private sealed class TagDbRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public long Version { get; set; }
    }
}
