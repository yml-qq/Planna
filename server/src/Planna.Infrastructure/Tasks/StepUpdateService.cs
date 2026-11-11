using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class StepUpdateService(TaskWriteContext db, TimeProvider clock) : IUpdateStep
{
    public async Task<UpdateStepResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid stepId,
        UpdateStepRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || stepId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.Patch.Provided.Count == 0)
            return new(UpdateStepStatus.Invalid, Code: "INVALID_STEP");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PATCH /steps/" + stepId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(UpdateStepStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(UpdateStepStatus.Replayed, JsonSerializer.Deserialize<StepView>(previous.Response))
                : new(UpdateStepStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Steps.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == stepId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(UpdateStepStatus.NotFound, Code: "STEP_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(UpdateStepStatus.VersionConflict,
                Details: new VersionConflictDetails(stepId, row.Version, Diff(row, request.Patch)));

        var patch = request.Patch;
        var title = patch.Provided.Contains("title") ? patch.Title?.Trim() ?? "" : row.Title;
        var isDone = patch.Provided.Contains("is_done") ? patch.IsDone!.Value : row.IsDone;
        var position = patch.Provided.Contains("position") ? patch.Position!.Value : row.Position;
        var estimate = patch.Provided.Contains("estimate_minutes") ? patch.EstimateMinutes : row.EstimateMinutes;
        if (title.Length is < 1 or > 500 || position < 0 || estimate is < 1 or > 1440)
            return new(UpdateStepStatus.Invalid, Code: "INVALID_STEP");

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_steps SET title={title}, is_done={isDone}, position={position}, estimate_minutes={estimate}, version={newVersion} WHERE id={stepId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new StepView(stepId, title, isDone, position, estimate, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "step",
            EntityId = stepId,
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
        return new(UpdateStepStatus.Updated, view);
    }

    private static List<string> Diff(StepRow row, StepPatch patch)
    {
        var changed = new List<string>();
        if (patch.Provided.Contains("title") &&
            !string.Equals(patch.Title?.Trim(), row.Title, StringComparison.Ordinal))
            changed.Add("title");
        if (patch.Provided.Contains("is_done") && patch.IsDone != row.IsDone)
            changed.Add("is_done");
        if (patch.Provided.Contains("position") && patch.Position != row.Position)
            changed.Add("position");
        if (patch.Provided.Contains("estimate_minutes") && patch.EstimateMinutes != row.EstimateMinutes)
            changed.Add("estimate_minutes");
        return changed;
    }
}
