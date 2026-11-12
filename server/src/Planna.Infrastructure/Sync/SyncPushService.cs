using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
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

        return (mutation.EntityType, mutation.Op) switch
        {
            ("task", "upsert") => await UpsertAsync(ownerId, mutation, ct),
            ("task", "delete") => await DeleteAsync(ownerId, mutation, ct),
            ("habit", "upsert") => await UpsertHabitAsync(ownerId, mutation, ct),
            ("habit", "delete") => await DeleteHabitAsync(ownerId, mutation, ct),
            ("habit_log", "upsert") => await UpsertHabitLogAsync(ownerId, mutation, ct),
            _ => Rejected(mutation.MutationId)
        };
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
            db.Changes.Add(Change(ownerId, createdRevision, "task", mutation.EntityId, "upsert", 1, ToJson(createdView), now));
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
        db.Changes.Add(Change(ownerId, revision, "task", mutation.EntityId, "upsert", newVersion, ToJson(view), updatedAt));
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
        db.Changes.Add(Change(ownerId, revision, "task", mutation.EntityId, "delete", newVersion, tombstone, now));
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

    private static SyncChangeRow Change(Guid ownerId, long revision, string entityType, Guid entityId,
        string op, long version, JsonElement payload, DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            EntityType = entityType,
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

    private static readonly string[] HabitSchedules = ["daily", "weekly", "weekdays"];

    private async Task<SyncPushResult> UpsertHabitAsync(Guid ownerId, SyncMutation mutation,
        CancellationToken ct)
    {
        if (!HabitPayload.TryParse(mutation.Payload, out var payload))
            return Rejected(mutation.MutationId);
        var row = await LoadHabitAsync(ownerId, mutation.EntityId, ct, ct: ct);
        if (mutation.BaseVersion == 0)
        {
            if (!payload.HasTitle || !payload.HasScheduleType || !payload.HasTimeZone)
                return Rejected(mutation.MutationId);
            if (row is not null)
                return Conflict(mutation.MutationId, HabitJson(row), row.Version, true);
            var title = payload.Title!.Trim();
            var schedule = payload.ScheduleType!;
            if (!ValidHabit(title, schedule, payload.Weekdays, payload.TargetQuantity ?? 1,
                    payload.Unit ?? "count", payload.TimeZone!, out var weekdays))
                return Rejected(mutation.MutationId);
            var now = clock.GetUtcNow();
            await ExecuteAsync(db, """
                INSERT INTO habits(id, owner_id, title, schedule_type, weekdays, target_quantity, unit,
                    time_zone, version, created_at, updated_at)
                VALUES(@Id, @Owner, @Title, @Schedule, @Weekdays, @Target, @Unit, @Zone, 1, @Now, @Now)
                """, p =>
            {
                p.Add(new NpgsqlParameter("Id", mutation.EntityId));
                p.Add(new NpgsqlParameter("Owner", ownerId));
                p.Add(new NpgsqlParameter("Title", title));
                p.Add(new NpgsqlParameter("Schedule", schedule));
                p.Add(WeekdaysParameter(weekdays));
                p.Add(new NpgsqlParameter("Target", payload.TargetQuantity ?? 1));
                p.Add(new NpgsqlParameter("Unit", payload.Unit ?? "count"));
                p.Add(new NpgsqlParameter("Zone", payload.TimeZone!));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            }, ct);
            var revision = await BumpRevision(ownerId, ct);
            var created = new HabitRow
            {
                Id = mutation.EntityId,
                Title = title,
                ScheduleType = schedule,
                Weekdays = weekdays?.ToArray(),
                TargetQuantity = payload.TargetQuantity ?? 1,
                Unit = payload.Unit ?? "count",
                TimeZone = payload.TimeZone!,
                Version = 1,
                CreatedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime
            };
            var view = HabitJson(created);
            db.Changes.Add(Change(ownerId, revision, "habit", mutation.EntityId, "upsert", 1, view, now));
            db.Mutations.Add(new MutationRow
            {
                OwnerId = ownerId,
                MutationId = mutation.MutationId,
                RequestHash = Hash(mutation),
                Response = view.GetRawText(),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await transactionCommit(ct);
            return new SyncPushResult(mutation.MutationId, "accepted", 1, view, false);
        }

        if (row is null || row.DeletedAt is not null) return Rejected(mutation.MutationId);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, HabitJson(row), row.Version, true);
        var mergedTitle = payload.HasTitle ? payload.Title!.Trim() : row.Title;
        var mergedSchedule = payload.HasScheduleType ? payload.ScheduleType! : row.ScheduleType;
        var mergedWeekdays = payload.HasWeekdays
            ? payload.Weekdays
            : row.Weekdays?.ToList();
        if (!ValidHabit(mergedTitle, mergedSchedule, mergedWeekdays,
                payload.TargetQuantity ?? row.TargetQuantity, payload.Unit ?? row.Unit,
                payload.TimeZone ?? row.TimeZone, out var normalized))
            return Rejected(mutation.MutationId);
        var newVersion = row.Version + 1;
        var updatedAt = clock.GetUtcNow();
        await ExecuteAsync(db, """
            UPDATE habits SET title=@Title, schedule_type=@Schedule, weekdays=@Weekdays,
                target_quantity=@Target, unit=@Unit, time_zone=@Zone, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", mergedTitle));
            p.Add(new NpgsqlParameter("Schedule", mergedSchedule));
            p.Add(WeekdaysParameter(normalized));
            p.Add(new NpgsqlParameter("Target", payload.TargetQuantity ?? row.TargetQuantity));
            p.Add(new NpgsqlParameter("Unit", payload.Unit ?? row.Unit));
            p.Add(new NpgsqlParameter("Zone", payload.TimeZone ?? row.TimeZone));
            p.Add(new NpgsqlParameter("Version", newVersion));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = updatedAt.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", mutation.EntityId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var revision2 = await BumpRevision(ownerId, ct);
        var updated = new HabitRow
        {
            Id = mutation.EntityId,
            Title = mergedTitle,
            ScheduleType = mergedSchedule,
            Weekdays = normalized?.ToArray(),
            TargetQuantity = payload.TargetQuantity ?? row.TargetQuantity,
            Unit = payload.Unit ?? row.Unit,
            TimeZone = payload.TimeZone ?? row.TimeZone,
            Version = newVersion,
            CreatedAt = row.CreatedAt,
            UpdatedAt = updatedAt.UtcDateTime
        };
        var view2 = HabitJson(updated);
        db.Changes.Add(Change(ownerId, revision2, "habit", mutation.EntityId, "upsert", newVersion, view2, updatedAt));
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutation.MutationId,
            RequestHash = Hash(mutation),
            Response = view2.GetRawText(),
            CreatedAt = updatedAt
        });
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return new SyncPushResult(mutation.MutationId, "accepted", newVersion, view2, false);
    }

    private async Task<SyncPushResult> DeleteHabitAsync(Guid ownerId, SyncMutation mutation,
        CancellationToken ct)
    {
        if (mutation.BaseVersion < 1) return Rejected(mutation.MutationId);
        var row = await LoadHabitAsync(ownerId, mutation.EntityId, ct, ct: ct);
        if (row is null) return Rejected(mutation.MutationId);
        if (row.DeletedAt is not null)
            return new SyncPushResult(mutation.MutationId, "duplicate", row.Version, null, false);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, HabitJson(row), row.Version, true);
        var newVersion = row.Version + 1;
        var now = clock.GetUtcNow();
        await ExecuteAsync(db, """
            UPDATE habits SET deleted_at=@Now, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", newVersion));
            p.Add(new NpgsqlParameter("Id", mutation.EntityId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var revision = await BumpRevision(ownerId, ct);
        var tombstone = JsonSerializer.SerializeToElement(new { id = mutation.EntityId, version = newVersion, deleted_at = now });
        db.Changes.Add(Change(ownerId, revision, "habit", mutation.EntityId, "delete", newVersion, tombstone, now));
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

    private async Task<SyncPushResult> UpsertHabitLogAsync(Guid ownerId, SyncMutation mutation,
        CancellationToken ct)
    {
        if (!HabitLogPayload.TryParse(mutation.Payload, out var payload) ||
            !payload.HasHabitId || !payload.HasLogDate)
            return Rejected(mutation.MutationId);
        var quantity = payload.Quantity ?? 1;
        var status = payload.Status ?? "done";
        if (quantity is < 0 or > 1000 || status is not ("done" or "skipped"))
            return Rejected(mutation.MutationId);
        var habit = await LoadHabitAsync(ownerId, payload.HabitId!.Value, ct, ct: ct);
        if (habit is null || habit.DeletedAt is not null) return Rejected(mutation.MutationId);
        var row = await LoadHabitLogAsync(ownerId, mutation.EntityId, ct, ct);
        if (mutation.BaseVersion == 0)
        {
            var sameDay = await LoadHabitLogByDateAsync(ownerId, payload.HabitId.Value, payload.LogDate!.Value, ct);
            if (sameDay is not null)
                return Conflict(mutation.MutationId, HabitLogJson(sameDay), sameDay.Version, true);
            if (row is not null) return Rejected(mutation.MutationId);
            var now = clock.GetUtcNow();
            await ExecuteAsync(db, """
                INSERT INTO habit_logs(id, owner_id, habit_id, log_date, quantity, status, version,
                    created_at, updated_at)
                VALUES(@Id, @Owner, @Habit, @Date, @Quantity, @Status, 1, @Now, @Now)
                """, p =>
            {
                p.Add(new NpgsqlParameter("Id", mutation.EntityId));
                p.Add(new NpgsqlParameter("Owner", ownerId));
                p.Add(new NpgsqlParameter("Habit", payload.HabitId.Value));
                p.Add(new NpgsqlParameter("Date", NpgsqlDbType.Date) { Value = payload.LogDate.Value });
                p.Add(new NpgsqlParameter("Quantity", quantity));
                p.Add(new NpgsqlParameter("Status", status));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            }, ct);
            var revision = await BumpRevision(ownerId, ct);
            var created = new HabitLogRow
            {
                Id = mutation.EntityId,
                HabitId = payload.HabitId.Value,
                LogDate = payload.LogDate.Value,
                Quantity = quantity,
                Status = status,
                Version = 1
            };
            var view = HabitLogJson(created);
            db.Changes.Add(Change(ownerId, revision, "habit_log", mutation.EntityId, "upsert", 1, view, now));
            db.Mutations.Add(new MutationRow
            {
                OwnerId = ownerId,
                MutationId = mutation.MutationId,
                RequestHash = Hash(mutation),
                Response = view.GetRawText(),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await transactionCommit(ct);
            return new SyncPushResult(mutation.MutationId, "accepted", 1, view, false);
        }

        if (row is null) return Rejected(mutation.MutationId);
        if (row.Version != mutation.BaseVersion)
            return Conflict(mutation.MutationId, HabitLogJson(row), row.Version, true);
        if (payload.HabitId != row.HabitId || payload.LogDate != row.LogDate)
            return Rejected(mutation.MutationId);
        var newVersion = row.Version + 1;
        var updatedAt = clock.GetUtcNow();
        await ExecuteAsync(db, """
            UPDATE habit_logs SET quantity=@Quantity, status=@Status, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Quantity", quantity));
            p.Add(new NpgsqlParameter("Status", status));
            p.Add(new NpgsqlParameter("Version", newVersion));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = updatedAt.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", mutation.EntityId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var revision2 = await BumpRevision(ownerId, ct);
        var updated = new HabitLogRow
        {
            Id = row.Id,
            HabitId = row.HabitId,
            LogDate = row.LogDate,
            Quantity = quantity,
            Status = status,
            Version = newVersion
        };
        var view2 = HabitLogJson(updated);
        db.Changes.Add(Change(ownerId, revision2, "habit_log", mutation.EntityId, "upsert", newVersion, view2, updatedAt));
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutation.MutationId,
            RequestHash = Hash(mutation),
            Response = view2.GetRawText(),
            CreatedAt = updatedAt
        });
        await db.SaveChangesAsync(ct);
        await transactionCommit(ct);
        return new SyncPushResult(mutation.MutationId, "accepted", newVersion, view2, false);
    }

    private async Task<HabitRow?> LoadHabitAsync(Guid ownerId, Guid habitId, CancellationToken ignored,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<HabitRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", schedule_type AS "ScheduleType", weekdays AS "Weekdays",
                target_quantity AS "TargetQuantity", unit AS "Unit", time_zone AS "TimeZone",
                paused_at AS "PausedAt", version AS "Version", created_at AS "CreatedAt",
                updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM habits WHERE id=@Id AND owner_id=@Owner
            """, new { Id = habitId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<HabitLogRow?> LoadHabitLogAsync(Guid ownerId, Guid logId, CancellationToken ignored,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<HabitLogRow>(new CommandDefinition("""
            SELECT id AS "Id", habit_id AS "HabitId", log_date AS "LogDate", quantity AS "Quantity",
                status AS "Status", version AS "Version"
            FROM habit_logs WHERE id=@Id AND owner_id=@Owner
            """, new { Id = logId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<HabitLogRow?> LoadHabitLogByDateAsync(Guid ownerId, Guid habitId, DateOnly logDate,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<HabitLogRow>(new CommandDefinition("""
            SELECT id AS "Id", habit_id AS "HabitId", log_date AS "LogDate", quantity AS "Quantity",
                status AS "Status", version AS "Version"
            FROM habit_logs WHERE owner_id=@Owner AND habit_id=@Habit AND log_date=@Date
            """, new { Owner = ownerId, Habit = habitId, Date = logDate },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private static bool ValidHabit(string title, string scheduleType, IReadOnlyList<short>? weekdays,
        int target, string unit, string timeZone, out IReadOnlyList<short>? normalized)
    {
        normalized = null;
        if (title.Length is < 1 or > 200 || !HabitSchedules.Contains(scheduleType) ||
            target is < 1 or > 1000 || unit.Length is < 1 or > 20 ||
            string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 128)
            return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
        if (scheduleType == "weekly")
        {
            if (weekdays is null || weekdays.Count is < 1 or > 7) return false;
            var distinct = weekdays.Distinct().ToList();
            if (distinct.Any(day => day is < 1 or > 7) || distinct.Count != weekdays.Count) return false;
            normalized = distinct.Order().ToList();
            return true;
        }
        normalized = null;
        return true;
    }

    private static NpgsqlParameter WeekdaysParameter(IReadOnlyList<short>? weekdays) =>
        new("Weekdays", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
        {
            Value = weekdays is null ? DBNull.Value : weekdays.ToArray()
        };

    private static JsonElement HabitJson(HabitRow row) => JsonSerializer.SerializeToElement(new
    {
        id = row.Id,
        title = row.Title,
        schedule_type = row.ScheduleType,
        weekdays = row.Weekdays,
        target_quantity = row.TargetQuantity,
        unit = row.Unit,
        time_zone = row.TimeZone,
        paused_at = row.PausedAt,
        version = row.Version,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    });

    private static JsonElement HabitLogJson(HabitLogRow row) => JsonSerializer.SerializeToElement(new
    {
        id = row.Id,
        habit_id = row.HabitId,
        log_date = row.LogDate,
        quantity = row.Quantity,
        status = row.Status,
        version = row.Version
    });

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class HabitRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string ScheduleType { get; set; } = "daily";
        public short[]? Weekdays { get; set; }
        public int TargetQuantity { get; set; } = 1;
        public string Unit { get; set; } = "count";
        public string TimeZone { get; set; } = "";
        public DateTime? PausedAt { get; set; }
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private sealed class HabitLogRow
    {
        public Guid Id { get; set; }
        public Guid HabitId { get; set; }
        public DateOnly LogDate { get; set; }
        public int Quantity { get; set; }
        public string Status { get; set; } = "done";
        public long Version { get; set; }
    }

    private sealed class HabitPayload
    {
        public string? Title { get; set; }
        public bool HasTitle { get; set; }
        public string? ScheduleType { get; set; }
        public bool HasScheduleType { get; set; }
        public List<short>? Weekdays { get; set; }
        public bool HasWeekdays { get; set; }
        public int? TargetQuantity { get; set; }
        public string? Unit { get; set; }
        public string? TimeZone { get; set; }
        public bool HasTimeZone { get; set; }

        public static bool TryParse(JsonElement element, out HabitPayload payload)
        {
            payload = new HabitPayload();
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "title":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.Title = value.GetString();
                        payload.HasTitle = true;
                        break;
                    case "schedule_type":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.ScheduleType = value.GetString();
                        payload.HasScheduleType = true;
                        break;
                    case "weekdays":
                        if (value.ValueKind != JsonValueKind.Array) return false;
                        payload.Weekdays = [];
                        foreach (var item in value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt16(out var day))
                                return false;
                            payload.Weekdays.Add(day);
                        }
                        payload.HasWeekdays = true;
                        break;
                    case "target_quantity":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var target))
                            return false;
                        payload.TargetQuantity = target;
                        break;
                    case "unit":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.Unit = value.GetString();
                        break;
                    case "time_zone":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.TimeZone = value.GetString();
                        payload.HasTimeZone = true;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    private sealed class HabitLogPayload
    {
        public Guid? HabitId { get; set; }
        public bool HasHabitId { get; set; }
        public DateOnly? LogDate { get; set; }
        public bool HasLogDate { get; set; }
        public int? Quantity { get; set; }
        public string? Status { get; set; }

        public static bool TryParse(JsonElement element, out HabitLogPayload payload)
        {
            payload = new HabitLogPayload();
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "habit_id":
                        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var habitId) ||
                            habitId == Guid.Empty) return false;
                        payload.HabitId = habitId;
                        payload.HasHabitId = true;
                        break;
                    case "log_date":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        var raw = value.GetString() ?? "";
                        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var logDate)) return false;
                        payload.LogDate = logDate;
                        payload.HasLogDate = true;
                        break;
                    case "quantity":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var quantity))
                            return false;
                        payload.Quantity = quantity;
                        break;
                    case "status":
                        if (value.ValueKind != JsonValueKind.String) return false;
                        payload.Status = value.GetString();
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

}
