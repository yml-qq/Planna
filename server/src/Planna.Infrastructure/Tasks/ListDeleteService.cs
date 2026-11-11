using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class ListDeleteService(TaskWriteContext db, TimeProvider clock) : IDeleteTaskList
{
    public async Task<DeleteTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid listId,
        long expectedVersion, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || listId == Guid.Empty || expectedVersion < 1)
            return new(DeleteTaskListStatus.Invalid);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "DELETE /lists/" + listId + "\n" + expectedVersion.ToString(CultureInfo.InvariantCulture))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(DeleteTaskListStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(DeleteTaskListStatus.Replayed)
                : new(DeleteTaskListStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Lists.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == listId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(DeleteTaskListStatus.NotFound, Code: "LIST_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(DeleteTaskListStatus.VersionConflict,
                Details: new VersionConflictDetails(listId, row.Version, []));
        if (row.Kind == "inbox") return new(DeleteTaskListStatus.InboxProtected, Code: "LIST_INBOX_PROTECTED");
        var notEmpty = await db.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM tasks WHERE owner_id={ownerId} AND list_id={listId} AND deleted_at IS NULL) AS \"Value\"").SingleAsync(ct);
        if (notEmpty) return new(DeleteTaskListStatus.NotEmpty, Code: "LIST_NOT_EMPTY");

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_lists SET deleted_at={now}, version={newVersion}, updated_at={now} WHERE id={listId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var tombstone = JsonSerializer.Serialize(new
        {
            id = listId,
            version = newVersion,
            deleted_at = now
        });
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "list",
            EntityId = listId,
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
        return new(DeleteTaskListStatus.Deleted);
    }
}
