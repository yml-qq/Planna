using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class StepCreateService(TaskWriteContext db, TimeProvider clock) : ICreateStep
{
    public async Task<CreateStepResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateStepRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        var position = request.Position ?? 0m;
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            request.Id == Guid.Empty || title.Length is < 1 or > 500 || position < 0 ||
            request.EstimateMinutes is < 1 or > 1440)
            return new(CreateStepStatus.Invalid, Code: "INVALID_STEP");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST /tasks/" + taskId + "/steps\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(CreateStepStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(CreateStepStatus.Replayed, JsonSerializer.Deserialize<StepView>(previous.Response))
                : new(CreateStepStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var taskExists = await db.Tasks.AsNoTracking()
            .AnyAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (!taskExists) return new(CreateStepStatus.TaskNotFound, Code: "TASK_NOT_FOUND");
        var idExists = await db.Steps.AsNoTracking().AnyAsync(x => x.Id == request.Id, ct);
        if (idExists) return new(CreateStepStatus.Conflict, Code: "STEP_ID_CONFLICT");

        db.Steps.Add(new StepRow
        {
            Id = request.Id,
            OwnerId = ownerId,
            TaskId = taskId,
            Title = title,
            Position = position,
            EstimateMinutes = request.EstimateMinutes,
            Version = 1
        });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var now = clock.GetUtcNow();
        var view = new StepView(request.Id, title, false, position, request.EstimateMinutes, 1);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "step",
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
            return new(CreateStepStatus.Conflict, Code: "STEP_ID_CONFLICT");
        }
        await transaction.CommitAsync(ct);
        return new(CreateStepStatus.Created, view);
    }
}
