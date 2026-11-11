using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class TaskReopenService(TaskWriteContext db, TimeProvider clock) : IReopenTask
{
    public async Task<ReopenTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        ReopenTaskRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty || request.ExpectedVersion < 1)
            return new(ReopenTaskStatus.Invalid, Code: "INVALID_TASK");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "REOPEN /tasks/" + taskId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(ReopenTaskStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
        {
            if (previous.RequestHash != hash)
                return new(ReopenTaskStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
            using var stored = JsonDocument.Parse(previous.Response);
            return stored.RootElement.TryGetProperty("original_local_start", out _)
                ? new(ReopenTaskStatus.Replayed,
                    Occurrence: JsonSerializer.Deserialize<OccurrenceView>(previous.Response))
                : new(ReopenTaskStatus.Replayed,
                    JsonSerializer.Deserialize<TaskReadView>(previous.Response));
        }

        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(ReopenTaskStatus.NotFound, Code: "TASK_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(ReopenTaskStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, row.Version, []));

        var ruleKind = await db.Database.GetDbConnection().ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT kind FROM recurrences WHERE owner_id=@Owner AND task_id=@Task",
            new { Owner = ownerId, Task = taskId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        if (ruleKind == "rrule")
        {
            if (request.OccurrenceId is null)
                return new(ReopenTaskStatus.RecurringTask, Code: "RECURRING_TASK_REQUIRES_OCCURRENCE");
            return await ReopenOccurrenceAsync(ownerId, mutationId, taskId, request.OccurrenceId.Value,
                hash, ct);
        }
        if (request.OccurrenceId is not null)
            return new(ReopenTaskStatus.InvalidOccurrence, Code: "INVALID_OCCURRENCE");

        if (row.Status == "archived") return new(ReopenTaskStatus.InvalidState, Code: "INVALID_TASK_STATE");
        if (row.Status != "done") return new(ReopenTaskStatus.NotCompleted, Code: "NOT_COMPLETED");

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET status='todo', completed_at=NULL, version={newVersion}, updated_at={now} WHERE id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskReadView(taskId, row.ListId, row.Title, row.Description, "todo", row.Priority,
            row.StartAt, row.DueAt, row.DueLocalDate, row.AllDay, row.TimeZone, row.EstimateMinutes,
            null, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityId = taskId,
            EntityVersion = newVersion,
            Payload = JsonSerializer.Serialize(view),
            CreatedAt = now
        });
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
        return new(ReopenTaskStatus.Reopened, view);
    }

    private async Task<ReopenTaskResult> ReopenOccurrenceAsync(Guid ownerId, Guid mutationId, Guid taskId,
        Guid occurrenceId, string hash, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var dbTransaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var occurrence = await connection.QuerySingleOrDefaultAsync<OccurrenceDbRow>(new CommandDefinition("""
            SELECT id AS "Id", status AS "Status", version AS "Version",
                original_local_start AS "OriginalLocalStart", time_zone AS "TimeZone",
                due_at AS "DueAt", completed_at AS "CompletedAt"
            FROM task_occurrences
            WHERE id = @Id AND owner_id = @Owner AND series_task_id = @Task
            """, new { Id = occurrenceId, Owner = ownerId, Task = taskId },
            dbTransaction, cancellationToken: ct));
        if (occurrence is null) return new(ReopenTaskStatus.InvalidOccurrence, Code: "INVALID_OCCURRENCE");
        if (occurrence.Status != "done") return new(ReopenTaskStatus.NotCompleted, Code: "NOT_COMPLETED");

        var newVersion = occurrence.Version + 1;
        var now = clock.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE task_occurrences SET status='todo', completed_at=NULL, version=@Version WHERE id=@Id",
            new { Version = newVersion, Id = occurrence.Id }, dbTransaction, cancellationToken: ct));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new OccurrenceView(occurrence.Id, taskId, occurrence.OriginalLocalStart,
            occurrence.TimeZone, AsInstant(occurrence.DueAt), "todo", null, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "occurrence",
            EntityId = occurrence.Id,
            EntityVersion = newVersion,
            Payload = JsonSerializer.Serialize(view),
            CreatedAt = now
        });
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(view),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await db.Database.CurrentTransaction!.CommitAsync(ct);
        return new(ReopenTaskStatus.Reopened, Occurrence: view);
    }

    private static DateTimeOffset? AsInstant(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed class OccurrenceDbRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "todo";
        public long Version { get; set; }
        public DateTime OriginalLocalStart { get; set; }
        public string TimeZone { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
