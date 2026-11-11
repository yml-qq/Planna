using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Sync;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Sync;

// Batch entry point for offline mutations. Each mutation is atomic on its own; the batch is not all-or-nothing.
public sealed class SyncPushService(TaskWriteContext db, TimeProvider clock) : ISyncPushCommand
{
    public async Task<SyncPushResponse> ExecuteAsync(Guid ownerId, SyncPushRequest request, CancellationToken ct)
    {
        var results = new List<SyncPushResult>(request.Mutations.Count);
        foreach (var mutation in request.Mutations)
            results.Add(await ProcessAsync(ownerId, mutation, ct));
        return new SyncPushResponse(results);
    }

    private async Task<SyncPushResult> ProcessAsync(Guid ownerId, SyncMutation mutation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return Rejected(mutation.MutationId);

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutation.MutationId, ct);
        if (previous is not null)
        {
            using var stored = JsonDocument.Parse(previous.Response);
            var element = stored.RootElement.Clone();
            long? version = element.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64() : null;
            return new SyncPushResult(mutation.MutationId, "duplicate", version, element, false);
        }

        if (mutation.EntityType != "task" || mutation.Op is not ("upsert" or "delete"))
            return Rejected(mutation.MutationId);

        return mutation.Op == "upsert"
            ? await UpsertAsync(ownerId, mutation, ct)
            : await DeleteAsync(ownerId, mutation, ct);
    }

    private async Task<SyncPushResult> UpsertAsync(Guid ownerId, SyncMutation mutation, CancellationToken ct)
    {
        if (!TaskPayload.TryParse(mutation.Payload, out var payload))
            return Rejected(mutation.MutationId);
        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == mutation.EntityId && x.OwnerId == ownerId, ct);

        if (mutation.BaseVersion == 0)
        {
            if (!payload.HasListId || !payload.HasTitle) return Rejected(mutation.MutationId);
            if (row is not null)
                return Conflict(mutation.MutationId, row.DeletedAt is null ? Project(row) : null, row.Version, true);
            if (!await ListOwned(ownerId, payload.ListId!.Value, ct)) return Rejected(mutation.MutationId);

            var title = payload.Title!.Trim();
            var timeZone = payload.TimeZone ?? "Asia/Shanghai";
            if (!Valid(title, payload.Description ?? "", payload.Priority ?? 0, payload.EstimateMinutes,
                    payload.AllDay ?? false, payload.DueAt, payload.DueLocalDate, payload.StartAt, timeZone))
                return Rejected(mutation.MutationId);

            var now = clock.GetUtcNow();
            var created = new TaskRow
            {
                Id = mutation.EntityId,
                OwnerId = ownerId,
                ListId = payload.ListId.Value,
                Title = title,
                Description = payload.Description ?? "",
                Priority = payload.Priority ?? 0,
                AllDay = payload.AllDay ?? false,
                StartAt = payload.StartAt?.ToUniversalTime(),
                DueAt = payload.DueAt?.ToUniversalTime(),
                DueLocalDate = payload.DueLocalDate,
                TimeZone = timeZone,
                EstimateMinutes = payload.EstimateMinutes,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Tasks.Add(created);
            var createdRevision = await BumpRevision(ownerId, ct);
            var createdView = ToView(created, 1);
            db.Changes.Add(Change(ownerId, createdRevision, mutation.EntityId, "upsert", 1, ToJson(createdView), now));
            db.Mutations.Add(Mutation(ownerId, mutation, createdView, now));
            await db.SaveChangesAsync(ct);
            await transactionCommit(ct);
            return Accepted(mutation.MutationId, createdView);
        }

        if (row is null) return Rejected(mutation.MutationId);
        if (row.DeletedAt is not null)
            return Conflict(mutation.MutationId, null, row.Version, false);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, Project(row), row.Version, true);

        var mergedTitle = (payload.HasTitle ? payload.Title!.Trim() : row.Title).Trim();
        var mergedDescription = payload.HasDescription ? payload.Description ?? "" : row.Description;
        var mergedPriority = payload.HasPriority ? payload.Priority!.Value : row.Priority;
        var mergedAllDay = payload.HasAllDay ? payload.AllDay!.Value : row.AllDay;
        var mergedStartAt = payload.HasStartAt ? payload.StartAt : row.StartAt;
        var mergedDueAt = payload.HasDueAt ? payload.DueAt : row.DueAt;
        var mergedDueLocal = payload.HasDueLocalDate ? payload.DueLocalDate : row.DueLocalDate;
        var mergedTimeZone = payload.HasTimeZone ? payload.TimeZone! : row.TimeZone;
        var mergedEstimate = payload.HasEstimateMinutes ? payload.EstimateMinutes : row.EstimateMinutes;
        if (!Valid(mergedTitle, mergedDescription, mergedPriority, mergedEstimate,
                mergedAllDay, mergedDueAt, mergedDueLocal, mergedStartAt, mergedTimeZone))
            return Rejected(mutation.MutationId);

        var newVersion = row.Version + 1;
        var updatedAt = clock.GetUtcNow();
        var utcDue = mergedDueAt?.ToUniversalTime();
        var utcStart = mergedStartAt?.ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET title={mergedTitle}, description={mergedDescription}, priority={mergedPriority}, start_at={utcStart}, due_at={utcDue}, due_local_date={mergedDueLocal}, all_day={mergedAllDay}, time_zone={mergedTimeZone}, estimate_minutes={mergedEstimate}, version={newVersion}, updated_at={updatedAt} WHERE id={mutation.EntityId}", ct);
        var revision = await BumpRevision(ownerId, ct);
        var view = new TaskReadView(mutation.EntityId, row.ListId, mergedTitle, mergedDescription, row.Status,
            mergedPriority, utcStart, utcDue, mergedDueLocal, mergedAllDay, mergedTimeZone, mergedEstimate,
            row.CompletedAt, newVersion);
        db.Changes.Add(Change(ownerId, revision, mutation.EntityId, "upsert", newVersion, ToJson(view), updatedAt));
        db.Mutations.Add(Mutation(ownerId, mutation, view, updatedAt));
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return Accepted(mutation.MutationId, view);
    }

    private async Task<SyncPushResult> DeleteAsync(Guid ownerId, SyncMutation mutation, CancellationToken ct)
    {
        if (mutation.BaseVersion < 1) return Rejected(mutation.MutationId);
        var row = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == mutation.EntityId && x.OwnerId == ownerId, ct);
        if (row is null) return Rejected(mutation.MutationId);
        if (row.DeletedAt is not null)
            return new SyncPushResult(mutation.MutationId, "duplicate", row.Version, null, false);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, Project(row), row.Version, true);

        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET deleted_at={now}, version={newVersion}, updated_at={now} WHERE id={mutation.EntityId}", ct);
        var revision = await BumpRevision(ownerId, ct);
        var tombstone = JsonSerializer.SerializeToElement(new { id = mutation.EntityId, version = newVersion, deleted_at = now });
        db.Changes.Add(Change(ownerId, revision, mutation.EntityId, "delete", newVersion, tombstone, now));
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutation.MutationId,
            RequestHash = Hash(mutation),
            Response = JsonSerializer.Serialize(tombstone),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return new SyncPushResult(mutation.MutationId, "accepted", newVersion, tombstone, false);
    }

    private async Task<bool> ListOwned(Guid ownerId, Guid listId, CancellationToken ct) =>
        await db.Lists.AsNoTracking().AnyAsync(x => x.Id == listId && x.OwnerId == ownerId && x.DeletedAt == null, ct);

    private static bool Valid(string title, string description, short priority, short? estimate,
        bool allDay, DateTimeOffset? dueAt, DateOnly? dueLocalDate, DateTimeOffset? startAt, string timeZone)
    {
        if (title.Length is < 1 or > 500 || description.Length > 20000 || priority is < 0 or > 3 ||
            estimate is < 1 or > 1440 || string.IsNullOrWhiteSpace(timeZone) ||
            allDay && dueAt is not null || !allDay && dueLocalDate is not null ||
            dueAt < startAt)
            return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private async Task<long> BumpRevision(Guid ownerId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        return await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
    }

    private Task transactionCommit(CancellationToken ct) => db.Database.CurrentTransaction!.CommitAsync(ct);

    private static SyncChangeRow Change(Guid ownerId, long revision, Guid entityId, string op, long version,
        JsonElement payload, DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityId = entityId,
            Op = op,
            EntityVersion = version,
            Payload = payload.GetRawText(),
            CreatedAt = now
        };

    private static MutationRow Mutation(Guid ownerId, SyncMutation mutation, TaskReadView view, DateTimeOffset now) => new()
    {
        OwnerId = ownerId,
        MutationId = mutation.MutationId,
        RequestHash = Hash(mutation),
        Response = JsonSerializer.Serialize(view),
        CreatedAt = now
    };

    private static string Hash(SyncMutation mutation) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        mutation.EntityId + "\n" + mutation.Op + "\n" + mutation.BaseVersion + "\n" + mutation.Payload.GetRawText())));

    private static JsonElement Project(TaskRow row) => ToJson(ToView(row, row.Version));

    private static TaskReadView ToView(TaskRow row, long version) => new(row.Id, row.ListId, row.Title,
        row.Description, row.Status, row.Priority, row.StartAt, row.DueAt, row.DueLocalDate, row.AllDay,
        row.TimeZone, row.EstimateMinutes, row.CompletedAt, version);

    private static JsonElement ToJson(TaskReadView view) => JsonSerializer.SerializeToElement(view);

    private static SyncPushResult Accepted(Guid mutationId, TaskReadView view) =>
        new(mutationId, "accepted", view.Version, ToJson(view), false);

    private static SyncPushResult Rejected(Guid mutationId) => new(mutationId, "rejected", null, null, false);

    private static SyncPushResult Conflict(Guid mutationId, JsonElement? serverEntity, long version, bool retryable) =>
        new(mutationId, "conflict", version, serverEntity, retryable);

    private sealed class TaskPayload
    {
        public Guid? ListId { get; set; }
        public bool HasListId { get; set; }
        public string? Title { get; set; }
        public bool HasTitle { get; set; }
        public string? Description { get; set; }
        public bool HasDescription { get; set; }
        public short? Priority { get; set; }
        public bool HasPriority { get; set; }
        public DateTimeOffset? StartAt { get; set; }
        public bool HasStartAt { get; set; }
        public DateTimeOffset? DueAt { get; set; }
        public bool HasDueAt { get; set; }
        public DateOnly? DueLocalDate { get; set; }
        public bool HasDueLocalDate { get; set; }
        public bool? AllDay { get; set; }
        public bool HasAllDay { get; set; }
        public string? TimeZone { get; set; }
        public bool HasTimeZone { get; set; }
        public short? EstimateMinutes { get; set; }
        public bool HasEstimateMinutes { get; set; }

        public static bool TryParse(JsonElement element, out TaskPayload payload)
        {
            payload = new TaskPayload();
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "list_id":
                        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var listId) ||
                            listId == Guid.Empty) return false;
                        payload.ListId = listId;
                        payload.HasListId = true;
                        break;
                    case "title":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.Title = value.GetString();
                        payload.HasTitle = true;
                        break;
                    case "description":
                        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                        payload.Description = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                        payload.HasDescription = true;
                        break;
                    case "priority":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt16(out var priority)) return false;
                        payload.Priority = priority;
                        payload.HasPriority = true;
                        break;
                    case "start_at":
                        if (value.ValueKind == JsonValueKind.Null) payload.StartAt = null;
                        else if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var start))
                            payload.StartAt = start;
                        else return false;
                        payload.HasStartAt = true;
                        break;
                    case "due_at":
                        if (value.ValueKind == JsonValueKind.Null) payload.DueAt = null;
                        else if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var due))
                            payload.DueAt = due;
                        else return false;
                        payload.HasDueAt = true;
                        break;
                    case "due_local_date":
                        if (value.ValueKind == JsonValueKind.Null) payload.DueLocalDate = null;
                        else if (value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var local))
                            payload.DueLocalDate = DateOnly.FromDateTime(local);
                        else return false;
                        payload.HasDueLocalDate = true;
                        break;
                    case "all_day":
                        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        payload.AllDay = value.GetBoolean();
                        payload.HasAllDay = true;
                        break;
                    case "time_zone":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.TimeZone = value.GetString();
                        payload.HasTimeZone = true;
                        break;
                    case "estimate_minutes":
                        if (value.ValueKind == JsonValueKind.Null) payload.EstimateMinutes = null;
                        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt16(out var estimate))
                            payload.EstimateMinutes = estimate;
                        else return false;
                        payload.HasEstimateMinutes = true;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }
}
