using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tags;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tags;

public sealed class TagCreateService(TaskWriteContext db, TimeProvider clock) : ICreateTag
{
    public async Task<CreateTagResult> ExecuteAsync(Guid ownerId, Guid mutationId,
        CreateTagRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var color = request.Color?.Trim();
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || request.Id == Guid.Empty ||
            name.Length is < 1 or > 60 || (color?.Length ?? 0) > 32)
            return new(CreateTagStatus.Invalid, Code: "INVALID_TAG");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST /tags\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(CreateTagStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(CreateTagStatus.Replayed, JsonSerializer.Deserialize<TagView>(previous.Response))
                : new(CreateTagStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var idExists = await db.Tags.AsNoTracking().AnyAsync(x => x.Id == request.Id, ct);
        if (idExists) return new(CreateTagStatus.Conflict, Code: "TAG_ID_CONFLICT");
        var nameExists = await db.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM tags WHERE owner_id={ownerId} AND lower(name)=lower({name}) AND deleted_at IS NULL) AS \"Value\"").SingleAsync(ct);
        if (nameExists) return new(CreateTagStatus.NameConflict, Code: "TAG_NAME_CONFLICT");

        db.Tags.Add(new TagRow
        {
            Id = request.Id,
            OwnerId = ownerId,
            Name = name,
            Color = color,
            Version = 1
        });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TagView(request.Id, name, color, 1);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "tag",
            EntityId = request.Id,
            EntityVersion = 1,
            Payload = JsonSerializer.Serialize(view),
            CreatedAt = clock.GetUtcNow()
        });
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(view),
            CreatedAt = clock.GetUtcNow()
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return new(CreateTagStatus.NameConflict, Code: "TAG_NAME_CONFLICT");
        }
        await transaction.CommitAsync(ct);
        return new(CreateTagStatus.Created, view);
    }
}
