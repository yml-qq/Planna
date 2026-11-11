using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class ListUpdateService(TaskWriteContext db, TimeProvider clock) : IUpdateTaskList
{
    public async Task<UpdateTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid listId,
        UpdateTaskListRequest request, CancellationToken ct)
    {
        var name = request.Patch.Name?.Trim() ?? "";
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || listId == Guid.Empty ||
            request.ExpectedVersion < 1 || name.Length is < 1 or > 120)
            return new(UpdateTaskListStatus.Invalid, Code: "INVALID_LIST");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PATCH /lists/" + listId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(UpdateTaskListStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(UpdateTaskListStatus.Replayed, JsonSerializer.Deserialize<TaskListView>(previous.Response))
                : new(UpdateTaskListStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Lists.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == listId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(UpdateTaskListStatus.NotFound, Code: "LIST_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(UpdateTaskListStatus.VersionConflict, Details: new VersionConflictDetails(
                listId, row.Version, string.Equals(name, row.Name, StringComparison.Ordinal) ? [] : ["name"]));

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_lists SET name={name}, version={newVersion}, updated_at={now} WHERE id={listId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskListView(listId, name, row.Kind, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "list",
            EntityId = listId,
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
        return new(UpdateTaskListStatus.Updated, view);
    }
}
