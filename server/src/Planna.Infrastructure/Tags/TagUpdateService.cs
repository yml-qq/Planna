using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tags;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tags;

public sealed class TagUpdateService(TaskWriteContext db, TimeProvider clock) : IUpdateTag
{
    public async Task<UpdateTagResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid tagId,
        UpdateTagRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || tagId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.Patch.Provided.Count == 0)
            return new(UpdateTagStatus.Invalid, Code: "INVALID_TAG");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PATCH /tags/" + tagId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(UpdateTagStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(UpdateTagStatus.Replayed, JsonSerializer.Deserialize<TagView>(previous.Response))
                : new(UpdateTagStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tags.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tagId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(UpdateTagStatus.NotFound, Code: "TAG_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(UpdateTagStatus.VersionConflict, Details: new TagConflictDetails(
                tagId, row.Version, Diff(row, request.Patch)));

        var name = request.Patch.Provided.Contains("name") ? request.Patch.Name?.Trim() ?? "" : row.Name;
        var color = request.Patch.Provided.Contains("color") ? request.Patch.Color?.Trim() : row.Color;
        if (name.Length is < 1 or > 60 || (color?.Length ?? 0) > 32)
            return new(UpdateTagStatus.Invalid, Code: "INVALID_TAG");
        var nameExists = await db.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM tags WHERE owner_id={ownerId} AND lower(name)=lower({name}) AND deleted_at IS NULL AND id<>{tagId}) AS \"Value\"").SingleAsync(ct);
        if (nameExists) return new(UpdateTagStatus.NameConflict, Code: "TAG_NAME_CONFLICT");

        var newVersion = row.Version + 1;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tags SET name={name}, color={color}, version={newVersion} WHERE id={tagId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var now = clock.GetUtcNow();
        var view = new TagView(tagId, name, color, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "tag",
            EntityId = tagId,
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
        return new(UpdateTagStatus.Updated, view);
    }

    private static List<string> Diff(TagRow row, TagPatch patch)
    {
        var changed = new List<string>();
        if (patch.Provided.Contains("name") &&
            !string.Equals(patch.Name?.Trim(), row.Name, StringComparison.Ordinal))
            changed.Add("name");
        if (patch.Provided.Contains("color") &&
            !string.Equals(patch.Color?.Trim(), row.Color, StringComparison.Ordinal))
            changed.Add("color");
        return changed;
    }
}
