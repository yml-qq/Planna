using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tags;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tags;

public sealed class TagDeleteService(TaskWriteContext db, TimeProvider clock) : IDeleteTag
{
    public async Task<DeleteTagResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid tagId,
        long expectedVersion, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || tagId == Guid.Empty || expectedVersion < 1)
            return new(DeleteTagStatus.Invalid);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "DELETE /tags/" + tagId + "\n" + expectedVersion.ToString(CultureInfo.InvariantCulture))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(DeleteTagStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(DeleteTagStatus.Replayed)
                : new(DeleteTagStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tags.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tagId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(DeleteTagStatus.NotFound, Code: "TAG_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(DeleteTagStatus.VersionConflict, Details: new TagConflictDetails(tagId, row.Version, []));

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tags SET deleted_at={now}, version={newVersion} WHERE id={tagId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var tombstone = JsonSerializer.Serialize(new
        {
            id = tagId,
            version = newVersion,
            deleted_at = now
        });
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "tag",
            EntityId = tagId,
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
        return new(DeleteTagStatus.Deleted);
    }
}
