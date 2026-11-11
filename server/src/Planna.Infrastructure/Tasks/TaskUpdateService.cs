using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class TaskUpdateService(TaskWriteContext db, TimeProvider clock) : IUpdateTask
{
    public async Task<UpdateTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        UpdateTaskRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.Patch.Provided.Count == 0)
            return new(UpdateTaskStatus.Invalid, Code: "INVALID_TASK");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PATCH /tasks/" + taskId + "\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize this owner's writes and reject deleted accounts before any state is read.
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(UpdateTaskStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(UpdateTaskStatus.Replayed, JsonSerializer.Deserialize<TaskReadView>(previous.Response))
                : new(UpdateTaskStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(UpdateTaskStatus.NotFound, Code: "TASK_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(UpdateTaskStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, row.Version, Diff(row, request.Patch)));

        var patch = request.Patch;
        var title = patch.Provided.Contains("title") ? patch.Title! : row.Title;
        var description = patch.Provided.Contains("description") ? patch.Description ?? "" : row.Description;
        var priority = patch.Provided.Contains("priority") ? patch.Priority!.Value : row.Priority;
        var dueAt = patch.Provided.Contains("due_at") ? patch.DueAt : row.DueAt;
        var dueLocalDate = patch.Provided.Contains("due_local_date") ? patch.DueLocalDate : row.DueLocalDate;
        var timeZone = patch.Provided.Contains("time_zone") ? patch.TimeZone! : row.TimeZone;
        var estimate = patch.Provided.Contains("estimate_minutes") ? patch.EstimateMinutes : row.EstimateMinutes;

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 500 ||
            description.Length > 20000 || priority is < 0 or > 3 ||
            estimate is < 1 or > 1440 ||
            row.AllDay && dueAt is not null || !row.AllDay && dueLocalDate is not null ||
            dueAt < row.StartAt ||
            string.IsNullOrWhiteSpace(timeZone))
            return new(UpdateTaskStatus.Invalid, Code: "INVALID_TASK");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (TimeZoneNotFoundException) { return new(UpdateTaskStatus.Invalid, Code: "INVALID_TIME_ZONE"); }
        catch (InvalidTimeZoneException) { return new(UpdateTaskStatus.Invalid, Code: "INVALID_TIME_ZONE"); }

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        title = title.Trim();
        var utcDue = dueAt?.ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET title={title}, description={description}, priority={priority}, due_at={utcDue}, due_local_date={dueLocalDate}, time_zone={timeZone}, estimate_minutes={estimate}, version={newVersion}, updated_at={now} WHERE id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskReadView(taskId, row.ListId, title, description, row.Status, priority,
            row.StartAt, utcDue, dueLocalDate, row.AllDay, timeZone, estimate,
            row.CompletedAt, newVersion);
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
        return new(UpdateTaskStatus.Updated, view);
    }

    private static List<string> Diff(TaskRow row, TaskPatch patch)
    {
        var changed = new List<string>();
        if (patch.Provided.Contains("title") && !string.Equals(patch.Title, row.Title, StringComparison.Ordinal))
            changed.Add("title");
        if (patch.Provided.Contains("description") && !string.Equals(patch.Description ?? "", row.Description, StringComparison.Ordinal))
            changed.Add("description");
        if (patch.Provided.Contains("priority") && patch.Priority != row.Priority)
            changed.Add("priority");
        if (patch.Provided.Contains("due_at") && patch.DueAt != row.DueAt)
            changed.Add("due_at");
        if (patch.Provided.Contains("due_local_date") && patch.DueLocalDate != row.DueLocalDate)
            changed.Add("due_local_date");
        if (patch.Provided.Contains("time_zone") && !string.Equals(patch.TimeZone, row.TimeZone, StringComparison.Ordinal))
            changed.Add("time_zone");
        if (patch.Provided.Contains("estimate_minutes") && patch.EstimateMinutes != row.EstimateMinutes)
            changed.Add("estimate_minutes");
        return changed;
    }
}
