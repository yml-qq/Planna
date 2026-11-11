using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class TaskCompleteService(TaskWriteContext db, TimeProvider clock) : ICompleteTask
{
    public async Task<CompleteTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CompleteTaskRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty || request.ExpectedVersion < 1)
            return new(CompleteTaskStatus.Invalid, Code: "INVALID_TASK");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "COMPLETE /tasks/" + taskId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(CompleteTaskStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(CompleteTaskStatus.Replayed, JsonSerializer.Deserialize<TaskReadView>(previous.Response))
                : new(CompleteTaskStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(CompleteTaskStatus.NotFound, Code: "TASK_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(CompleteTaskStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, row.Version, []));
        if (row.Status == "done") return new(CompleteTaskStatus.AlreadyCompleted, Code: "ALREADY_COMPLETED");
        if (row.Status == "archived") return new(CompleteTaskStatus.InvalidState, Code: "INVALID_TASK_STATE");
        var recurring = await db.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM recurrences WHERE owner_id={ownerId} AND task_id={taskId}) AS \"Value\"").SingleAsync(ct);
        if (recurring)
        {
            if (request.OccurrenceId is null)
                return new(CompleteTaskStatus.RecurringTask, Code: "RECURRING_TASK_REQUIRES_OCCURRENCE");
            var connection = db.Database.GetDbConnection();
            var dbTransaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var occurrence = await connection.QuerySingleOrDefaultAsync<OccurrenceDbRow>(new CommandDefinition("""
                SELECT id AS "Id", status AS "Status", version AS "Version",
                    original_local_start AS "OriginalLocalStart", time_zone AS "TimeZone",
                    due_at AS "DueAt", completed_at AS "CompletedAt"
                FROM task_occurrences
                WHERE id = @Id AND owner_id = @Owner AND series_task_id = @Task
                """, new { Id = request.OccurrenceId.Value, Owner = ownerId, Task = taskId },
                dbTransaction, cancellationToken: ct));
            if (occurrence is null) return new(CompleteTaskStatus.InvalidOccurrence, Code: "INVALID_OCCURRENCE");
            if (occurrence.Status == "done")
                return new(CompleteTaskStatus.AlreadyCompleted, Code: "ALREADY_COMPLETED");
            var completedNow = clock.GetUtcNow();
            var occurrenceCompletedAt = request.CompletedAt?.ToUniversalTime() ?? completedNow;
            if (occurrenceCompletedAt < row.CreatedAt) occurrenceCompletedAt = row.CreatedAt;
            if (occurrenceCompletedAt > completedNow) occurrenceCompletedAt = completedNow;
            var occurrenceVersion = occurrence.Version + 1;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE task_occurrences SET status='done', completed_at=@CompletedAt, version=@Version WHERE id=@Id",
                new { CompletedAt = occurrenceCompletedAt.UtcDateTime, Version = occurrenceVersion, Id = occurrence.Id },
                dbTransaction, cancellationToken: ct));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
            var occurrenceRevision = await db.Database.SqlQuery<long>(
                $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
            var occurrenceView = new OccurrenceView(occurrence.Id, taskId, occurrence.OriginalLocalStart,
                occurrence.TimeZone, AsInstant(occurrence.DueAt), "done", occurrenceCompletedAt, occurrenceVersion);
            db.Changes.Add(new SyncChangeRow
            {
                OwnerId = ownerId,
                Revision = occurrenceRevision,
                EntityType = "occurrence",
                EntityId = occurrence.Id,
                EntityVersion = occurrenceVersion,
                Payload = JsonSerializer.Serialize(occurrenceView),
                CreatedAt = completedNow
            });
            db.Mutations.Add(new MutationRow
            {
                OwnerId = ownerId,
                MutationId = mutationId,
                RequestHash = hash,
                Response = JsonSerializer.Serialize(occurrenceView),
                CreatedAt = completedNow
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(CompleteTaskStatus.Completed, Occurrence: occurrenceView);
        }
        if (request.OccurrenceId is not null) return new(CompleteTaskStatus.InvalidOccurrence, Code: "INVALID_OCCURRENCE");

        var now = clock.GetUtcNow();
        var completedAt = request.CompletedAt?.ToUniversalTime() ?? now;
        if (completedAt < row.CreatedAt) completedAt = row.CreatedAt;
        if (completedAt > now) completedAt = now;

        var newVersion = row.Version + 1;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET status='done', completed_at={completedAt}, version={newVersion}, updated_at={now} WHERE id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskReadView(taskId, row.ListId, row.Title, row.Description, "done", row.Priority,
            row.StartAt, row.DueAt, row.DueLocalDate, row.AllDay, row.TimeZone, row.EstimateMinutes,
            completedAt, newVersion);
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
        return new(CompleteTaskStatus.Completed, view);
    }

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

    private static DateTimeOffset? AsInstant(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
