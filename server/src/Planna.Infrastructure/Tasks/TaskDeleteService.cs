using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class TaskDeleteService(TaskWriteContext db, TimeProvider clock) : IDeleteTask
{
    public async Task<DeleteTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        long expectedVersion, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty || expectedVersion < 1)
            return new(DeleteTaskStatus.Invalid);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "DELETE /tasks/" + taskId + "\n" + expectedVersion.ToString(CultureInfo.InvariantCulture))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(DeleteTaskStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(DeleteTaskStatus.Replayed)
                : new(DeleteTaskStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(DeleteTaskStatus.NotFound, Code: "TASK_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(DeleteTaskStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, row.Version, []));

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET deleted_at={now}, version={newVersion}, updated_at={now} WHERE id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var tombstone = JsonSerializer.Serialize(new
        {
            id = taskId,
            version = newVersion,
            deleted_at = now
        });
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityId = taskId,
            Op = "delete",
            EntityVersion = newVersion,
            Payload = tombstone,
            CreatedAt = now
        });
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = "{}",
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(DeleteTaskStatus.Deleted);
    }
}
