using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class SetTaskTagsService(TaskWriteContext db, TimeProvider clock) : ISetTaskTags
{
    public async Task<SetTaskTagsResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        ReplaceTaskTagsRequest request, CancellationToken ct)
    {
        var tagIds = request.TagIds?.Distinct().ToList() ?? [];
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            request.ExpectedVersion < 1 || tagIds.Count > 20 || tagIds.Any(x => x == Guid.Empty))
            return new(SetTaskTagsStatus.Invalid, Code: "INVALID_TAG_SET");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PUT /tasks/" + taskId + "/tags\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(SetTaskTagsStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(SetTaskTagsStatus.Replayed, JsonSerializer.Deserialize<TaskTagSetView>(previous.Response))
                : new(SetTaskTagsStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (row is null) return new(SetTaskTagsStatus.NotFound, Code: "TASK_NOT_FOUND");

        var current = (await db.Database.SqlQuery<Guid>(
            $"SELECT tag_id AS \"Value\" FROM task_tags WHERE owner_id={ownerId} AND task_id={taskId}").ToListAsync(ct))
            .ToHashSet();
        if (row.Version != request.ExpectedVersion)
        {
            var changed = current.SetEquals(tagIds) ? [] : new List<string> { "tag_ids" };
            return new(SetTaskTagsStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, row.Version, changed));
        }

        if (tagIds.Count > 0)
        {
            var owned = await db.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM tags WHERE owner_id={ownerId} AND deleted_at IS NULL AND id = ANY({tagIds.ToArray()})").SingleAsync(ct);
            if (owned != tagIds.Count) return new(SetTaskTagsStatus.InvalidTagSet, Code: "INVALID_TAG_SET");
        }

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM task_tags WHERE owner_id={ownerId} AND task_id={taskId}", ct);
        foreach (var tagId in tagIds)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO task_tags(owner_id, task_id, tag_id) VALUES({ownerId}, {taskId}, {tagId})", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET version={newVersion}, updated_at={now} WHERE id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new TaskReadView(taskId, row.ListId, row.Title, row.Description, row.Status, row.Priority,
            row.StartAt, row.DueAt, row.DueLocalDate, row.AllDay, row.TimeZone, row.EstimateMinutes,
            row.CompletedAt, newVersion);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(view))!.AsObject();
        payload["tag_ids"] = new JsonArray(tagIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityId = taskId,
            EntityVersion = newVersion,
            Payload = payload.ToJsonString(),
            CreatedAt = now
        });
        var set = new TaskTagSetView(taskId, newVersion, tagIds);
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(set),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(SetTaskTagsStatus.Updated, set);
    }
}
