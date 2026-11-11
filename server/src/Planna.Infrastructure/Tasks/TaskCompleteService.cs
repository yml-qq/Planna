using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
        if (recurring) return new(CompleteTaskStatus.RecurringTask, Code: "RECURRING_TASK_REQUIRES_OCCURRENCE");
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
}
