using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Goals;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Goals;

// Goals and milestones are a server-only aggregate in this slice; writes are idempotent and
// version checked, progress is derived from non-deleted milestones on read.
public sealed class GoalService(TaskWriteContext db, TimeProvider clock) : IGoalService
{
    private static readonly string[] GoalStatuses = ["active", "completed", "archived"];

    public async Task<GoalResult> ListAsync(Guid ownerId, CancellationToken ct)
    {
        var goals = (await db.Database.GetDbConnection().QueryAsync<GoalRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", description AS "Description",
                target_date AS "TargetDate", status AS "Status", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM goals WHERE owner_id=@Owner AND deleted_at IS NULL
            ORDER BY status, target_date NULLS LAST, created_at DESC, id
            """, new { Owner = ownerId }, db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: ct))).ToList();
        var milestones = (await db.Database.GetDbConnection().QueryAsync<MilestoneRow>(new CommandDefinition("""
            SELECT id AS "Id", goal_id AS "GoalId", title AS "Title", due_date AS "DueDate",
                completed AS "Completed", position AS "Position", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM milestones WHERE owner_id=@Owner AND deleted_at IS NULL
            ORDER BY position, created_at, id
            """, new { Owner = ownerId }, db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: ct))).ToList();
        var grouped = milestones.GroupBy(milestone => milestone.GoalId)
            .ToDictionary(group => group.Key, group => group.Select(ToView).ToList());
        var views = goals.Select(goal => ToView(goal,
            grouped.GetValueOrDefault(goal.Id) ?? [])).ToList();
        return new(GoalStatus.Ok, Goals: new GoalList(views));
    }

    public async Task<GoalResult> GetAsync(Guid ownerId, Guid goalId, CancellationToken ct)
    {
        var goal = await LoadGoalAsync(ownerId, goalId, forUpdate: false, ct);
        if (goal is null) return new(GoalStatus.NotFound, Code: "GOAL_NOT_FOUND");
        var milestones = await LoadMilestonesAsync(ownerId, goalId, ct);
        return new(GoalStatus.Ok, ToView(goal, milestones));
    }

    public async Task<GoalResult> CreateAsync(Guid ownerId, Guid mutationId,
        CreateGoalRequest request, CancellationToken ct)
    {
        if (!ValidTitle(request.Title) || request.Description?.Length > 2000 ||
            !TryDate(request.TargetDate, out var targetDate))
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("POST /goals\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(GoalStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync<GoalView>(ownerId, mutationId, hash, ct, goal => new(GoalStatus.Replayed, Goal: goal));
        if (replay is not null) return replay;

        var id = Guid.NewGuid();
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            INSERT INTO goals(id, owner_id, title, description, target_date, status, version,
                created_at, updated_at)
            VALUES(@Id, @Owner, @Title, @Description, @TargetDate, 'active', 1, @Now, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Title", request.Title!.Trim()));
            p.Add(new NpgsqlParameter("Description", request.Description ?? ""));
            p.Add(new NpgsqlParameter("TargetDate", NpgsqlDbType.Date)
            {
                Value = targetDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var view = new GoalView(id, request.Title!.Trim(), request.Description ?? "",
            FormatDate(targetDate), "active", 1, now, now, new GoalProgress(0, 0, 0), []);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Created, view);
    }

    public async Task<GoalResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid goalId,
        UpdateGoalRequest request, CancellationToken ct)
    {
        if (goalId == Guid.Empty || request.ExpectedVersion < 1 || request.Patch is null)
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("PATCH /goals/" + goalId + "\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync<GoalView>(ownerId, mutationId, hash, ct, goal => new(GoalStatus.Replayed, Goal: goal));
        if (replay is not null) return replay;
        var row = await LoadGoalAsync(ownerId, goalId, forUpdate: true, ct);
        if (row is null) return new(GoalStatus.NotFound, Code: "GOAL_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(GoalStatus.VersionConflict,
                Details: new VersionConflictDetails(goalId, row.Version, []));
        var patch = request.Patch;
        var title = patch.Title?.Trim() ?? row.Title;
        var description = patch.Description ?? row.Description;
        var status = patch.Status ?? row.Status;
        if (!ValidTitle(title) || description.Length > 2000 || !GoalStatuses.Contains(status) ||
            !TryDate(patch.TargetDate, out var targetDate, row.TargetDate))
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var now = Truncate(clock.GetUtcNow());
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE goals SET title=@Title, description=@Description, target_date=@TargetDate,
                status=@Status, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("Description", description));
            p.Add(new NpgsqlParameter("TargetDate", NpgsqlDbType.Date)
            {
                Value = targetDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Status", status));
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", goalId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var milestones = await LoadMilestonesAsync(ownerId, goalId, ct);
        var view = new GoalView(goalId, title, description, FormatDate(targetDate), status, version,
            Instant(row.CreatedAt), now, Progress(milestones), milestones);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Updated, view);
    }

    public async Task<GoalResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid goalId,
        long expectedVersion, CancellationToken ct)
    {
        if (goalId == Guid.Empty || expectedVersion < 1)
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("DELETE /goals/" + goalId + "\n" + expectedVersion);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync<GoalView>(ownerId, mutationId, hash, ct, _ => new(GoalStatus.Replayed));
        if (replay is not null) return replay;
        var row = await LoadGoalAsync(ownerId, goalId, forUpdate: true, ct);
        if (row is null) return new(GoalStatus.NotFound, Code: "GOAL_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(GoalStatus.VersionConflict,
                Details: new VersionConflictDetails(goalId, row.Version, []));
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            UPDATE goals SET deleted_at=@Now, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", row.Version + 1));
            p.Add(new NpgsqlParameter("Id", goalId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, new { deleted = true }, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Deleted);
    }

    public async Task<GoalResult> CreateMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        CreateMilestoneRequest request, CancellationToken ct)
    {
        if (goalId == Guid.Empty || !ValidTitle(request.Title) ||
            !TryDate(request.DueDate, out var dueDate))
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("POST /goals/" + goalId + "/milestones\n" + JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync<MilestoneView>(ownerId, mutationId, hash, ct,
            milestone => new(GoalStatus.Replayed, Milestone: milestone));
        if (replay is not null) return replay;
        var goal = await LoadGoalAsync(ownerId, goalId, forUpdate: true, ct);
        if (goal is null) return new(GoalStatus.NotFound, Code: "GOAL_NOT_FOUND");
        var id = Guid.NewGuid();
        var now = Truncate(clock.GetUtcNow());
        var position = await db.Database.SqlQuery<int>(
            $"SELECT coalesce(max(position),0)+1 AS \"Value\" FROM milestones WHERE owner_id={ownerId} AND goal_id={goalId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        await ExecuteAsync(db, """
            INSERT INTO milestones(id, owner_id, goal_id, title, due_date, completed, position,
                version, created_at, updated_at)
            VALUES(@Id, @Owner, @Goal, @Title, @DueDate, false, @Position, 1, @Now, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", id));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Goal", goalId));
            p.Add(new NpgsqlParameter("Title", request.Title!.Trim()));
            p.Add(new NpgsqlParameter("DueDate", NpgsqlDbType.Date)
            {
                Value = dueDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Position", position));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var view = new MilestoneView(id, goalId, request.Title!.Trim(), FormatDate(dueDate), false,
            position, 1, now, now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Created, Milestone: view);
    }

    public async Task<GoalResult> UpdateMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        Guid milestoneId, UpdateMilestoneRequest request, CancellationToken ct)
    {
        if (goalId == Guid.Empty || milestoneId == Guid.Empty || request.ExpectedVersion < 1 ||
            request.Patch is null)
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("PATCH /goals/" + goalId + "/milestones/" + milestoneId + "\n" +
                        JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync<MilestoneView>(ownerId, mutationId, hash, ct,
            milestone => new(GoalStatus.Replayed, Milestone: milestone));
        if (replay is not null) return replay;
        var row = await LoadMilestoneAsync(ownerId, goalId, milestoneId, ct);
        if (row is null) return new(GoalStatus.NotFound, Code: "MILESTONE_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(GoalStatus.VersionConflict,
                Details: new VersionConflictDetails(milestoneId, row.Version, []));
        var patch = request.Patch;
        var title = patch.Title?.Trim() ?? row.Title;
        var completed = patch.Completed ?? row.Completed;
        var position = patch.Position ?? row.Position;
        if (!ValidTitle(title) || position is < 0 or > 10000 ||
            !TryDate(patch.DueDate, out var dueDate, row.DueDate))
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var now = Truncate(clock.GetUtcNow());
        var version = row.Version + 1;
        await ExecuteAsync(db, """
            UPDATE milestones SET title=@Title, due_date=@DueDate, completed=@Completed,
                position=@Position, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Title", title));
            p.Add(new NpgsqlParameter("DueDate", NpgsqlDbType.Date)
            {
                Value = dueDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value
            });
            p.Add(new NpgsqlParameter("Completed", completed));
            p.Add(new NpgsqlParameter("Position", position));
            p.Add(new NpgsqlParameter("Version", version));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Id", milestoneId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var view = new MilestoneView(milestoneId, goalId, title, FormatDate(dueDate), completed,
            position, version, Instant(row.CreatedAt), now);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Updated, Milestone: view);
    }

    public async Task<GoalResult> DeleteMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        Guid milestoneId, long expectedVersion, CancellationToken ct)
    {
        if (goalId == Guid.Empty || milestoneId == Guid.Empty || expectedVersion < 1)
            return new(GoalStatus.Invalid, Code: "INVALID_GOAL");
        var hash = Hash("DELETE /goals/" + goalId + "/milestones/" + milestoneId + "\n" + expectedVersion);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ownerId, ct);
        var replay = await ReplayAsync<MilestoneView>(ownerId, mutationId, hash, ct,
            _ => new(GoalStatus.Replayed));
        if (replay is not null) return replay;
        var row = await LoadMilestoneAsync(ownerId, goalId, milestoneId, ct);
        if (row is null) return new(GoalStatus.NotFound, Code: "MILESTONE_NOT_FOUND");
        if (row.Version != expectedVersion)
            return new(GoalStatus.VersionConflict,
                Details: new VersionConflictDetails(milestoneId, row.Version, []));
        var now = Truncate(clock.GetUtcNow());
        await ExecuteAsync(db, """
            UPDATE milestones SET deleted_at=@Now, version=@Version, updated_at=@Now
            WHERE id=@Id AND owner_id=@Owner
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Version", row.Version + 1));
            p.Add(new NpgsqlParameter("Id", milestoneId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, new { deleted = true }, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(GoalStatus.Deleted);
    }

    private async Task<GoalRow?> LoadGoalAsync(Guid ownerId, Guid goalId, bool forUpdate,
        CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<GoalRow>(new CommandDefinition("""
            SELECT id AS "Id", title AS "Title", description AS "Description",
                target_date AS "TargetDate", status AS "Status", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM goals WHERE id=@Id AND owner_id=@Owner AND deleted_at IS NULL
            """ + (forUpdate ? " FOR UPDATE" : ""),
            new { Id = goalId, Owner = ownerId }, db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: ct));

    private async Task<MilestoneRow?> LoadMilestoneAsync(Guid ownerId, Guid goalId,
        Guid milestoneId, CancellationToken ct) =>
        await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<MilestoneRow>(new CommandDefinition("""
            SELECT id AS "Id", goal_id AS "GoalId", title AS "Title", due_date AS "DueDate",
                completed AS "Completed", position AS "Position", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM milestones
            WHERE id=@Id AND goal_id=@Goal AND owner_id=@Owner AND deleted_at IS NULL
            """, new { Id = milestoneId, Goal = goalId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));

    private async Task<IReadOnlyList<MilestoneView>> LoadMilestonesAsync(Guid ownerId, Guid goalId,
        CancellationToken ct)
    {
        var rows = await db.Database.GetDbConnection().QueryAsync<MilestoneRow>(new CommandDefinition("""
            SELECT id AS "Id", goal_id AS "GoalId", title AS "Title", due_date AS "DueDate",
                completed AS "Completed", position AS "Position", version AS "Version",
                created_at AS "CreatedAt", updated_at AS "UpdatedAt", deleted_at AS "DeletedAt"
            FROM milestones WHERE owner_id=@Owner AND goal_id=@Goal AND deleted_at IS NULL
            ORDER BY position, created_at, id
            """, new { Owner = ownerId, Goal = goalId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        return rows.Select(ToView).ToList();
    }

    private async Task<GoalResult?> ReplayAsync<T>(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct, Func<T, GoalResult> replay)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(GoalStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        if (stored.RootElement.TryGetProperty("deleted", out _)) return new(GoalStatus.Replayed);
        var value = JsonSerializer.Deserialize<T>(previous.Response);
        return value is null ? new(GoalStatus.Replayed) : replay(value);
    }

    private async Task LockAsync(Guid ownerId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"goal:" + ownerId.ToString("N")}, 0))", ct);

    private static GoalView ToView(GoalRow goal, IReadOnlyList<MilestoneView> milestones)
    {
        return new GoalView(goal.Id, goal.Title, goal.Description, FormatDate(goal.TargetDate),
            goal.Status, goal.Version, Instant(goal.CreatedAt), Instant(goal.UpdatedAt),
            Progress(milestones), milestones);
    }

    private static MilestoneView ToView(MilestoneRow milestone) => new(milestone.Id,
        milestone.GoalId, milestone.Title, FormatDate(milestone.DueDate), milestone.Completed,
        milestone.Position, milestone.Version, Instant(milestone.CreatedAt),
        Instant(milestone.UpdatedAt));

    private static GoalProgress Progress(IReadOnlyList<MilestoneView> milestones)
    {
        var completed = milestones.Count(milestone => milestone.Completed);
        var total = milestones.Count;
        var percent = total == 0 ? 0 : completed * 100 / total;
        return new GoalProgress(completed, total, percent);
    }

    private static bool ValidTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 200;

    private static bool TryDate(string? raw, out DateOnly? value, DateOnly? fallback = null)
    {
        value = fallback;
        if (raw is null) return true;
        if (raw.Length == 0)
        {
            value = null;
            return true;
        }
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static string? FormatDate(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static MutationRow Mutation(Guid ownerId, Guid mutationId, string hash, object response,
        DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(response),
            CreatedAt = now
        };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class GoalRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public DateOnly? TargetDate { get; set; }
        public string Status { get; set; } = "active";
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private sealed class MilestoneRow
    {
        public Guid Id { get; set; }
        public Guid GoalId { get; set; }
        public string Title { get; set; } = "";
        public DateOnly? DueDate { get; set; }
        public bool Completed { get; set; }
        public int Position { get; set; }
        public long Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
