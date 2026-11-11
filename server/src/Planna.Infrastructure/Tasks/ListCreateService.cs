using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class ListCreateService(TaskWriteContext db, TimeProvider clock) : ICreateTaskList
{
    public async Task<CreateTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId,
        CreateTaskListRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || request.Id == Guid.Empty ||
            name.Length is < 1 or > 120)
            return new(CreateTaskListStatus.Invalid, Code: "INVALID_LIST");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST /lists\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(CreateTaskListStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(CreateTaskListStatus.Replayed, JsonSerializer.Deserialize<TaskListView>(previous.Response))
                : new(CreateTaskListStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var exists = await db.Lists.AsNoTracking().AnyAsync(x => x.Id == request.Id, ct);
        if (exists) return new(CreateTaskListStatus.Conflict, Code: "LIST_ID_CONFLICT");

        var now = clock.GetUtcNow();
        db.Lists.Add(new TaskListRow
        {
            Id = request.Id,
            OwnerId = ownerId,
            Name = name,
            Kind = "custom",
            Version = 1
        });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskListView(request.Id, name, "custom", 1);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "list",
            EntityId = request.Id,
            EntityVersion = 1,
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
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return new(CreateTaskListStatus.Conflict, Code: "LIST_ID_CONFLICT");
        }
        await transaction.CommitAsync(ct);
        return new(CreateTaskListStatus.Created, view);
    }
}
