using Dapper;
using Npgsql;
using Planna.Application.Tasks;

namespace Planna.Infrastructure.Tasks;

// Read model only. Step writes stay in the EF Core step services.
public sealed class StepQuery(string connectionString) : IStepReadQuery
{
    public async Task<GetStepListResult> ListAsync(Guid ownerId, Guid taskId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM tasks WHERE id = @TaskId AND owner_id = @OwnerId AND deleted_at IS NULL)",
            new { TaskId = taskId, OwnerId = ownerId }, cancellationToken: ct));
        if (!exists) return new(GetStepListStatus.TaskNotFound);
        var rows = await connection.QueryAsync<StepDbRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", is_done AS "IsDone", position AS "Position",
                estimate_minutes AS "EstimateMinutes", version AS "Version"
            FROM task_steps
            WHERE owner_id = @OwnerId AND task_id = @TaskId AND deleted_at IS NULL
            ORDER BY position, id
            """, new { TaskId = taskId, OwnerId = ownerId }, cancellationToken: ct));
        return new(GetStepListStatus.Ok, rows.Select(x =>
            new StepView(x.Id, x.Title, x.IsDone, x.Position, x.EstimateMinutes, x.Version)).ToList());
    }

    private sealed class StepDbRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public bool IsDone { get; set; }
        public decimal Position { get; set; }
        public short? EstimateMinutes { get; set; }
        public long Version { get; set; }
    }
}
