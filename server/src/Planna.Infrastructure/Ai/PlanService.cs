using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Ai;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Ai;

public sealed class PlanService(TaskWriteContext db, TimeProvider clock, IPlanScheduler scheduler)
    : IProposePlan, IApplyPlan
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);
    private const int MaxRangeDays = 7;

    public async Task<PlanResult> ExecuteAsync(Guid ownerId, ProposePlanRequest request, CancellationToken ct)
    {
        var taskIds = request.TaskIds?.Distinct().ToList() ?? [];
        if (ownerId == Guid.Empty || taskIds.Count is < 1 or > 100 ||
            request.RangeStart >= request.RangeEnd ||
            request.RangeEnd - request.RangeStart > TimeSpan.FromDays(MaxRangeDays))
            return new(PlanStatus.Invalid, Code: "INVALID_PLAN");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tasks = await db.Tasks.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && taskIds.Contains(x.Id) && x.DeletedAt == null)
            .Select(x => new SchedulableTask(x.Id, x.Version, x.Title, x.EstimateMinutes))
            .ToListAsync(ct);
        if (tasks.Count != taskIds.Count) return new(PlanStatus.TaskNotFound, Code: "TASK_NOT_FOUND");
        var existing = (await db.Database.GetDbConnection().QueryAsync<BlockRow>(new CommandDefinition("""
            SELECT start_at AS "Start", end_at AS "End"
            FROM task_time_blocks
            WHERE owner_id = @Owner AND deleted_at IS NULL AND end_at > @Start AND start_at < @End
            """, new { Owner = ownerId, Start = request.RangeStart, End = request.RangeEnd },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct)))
            .Select(x => new ExistingBlock(x.Start, x.End)).ToList();

        var schedule = scheduler.Build(tasks.OrderBy(x => taskIds.IndexOf(x.Id)).ToList(),
            request.RangeStart, request.RangeEnd, existing);
        var snapshot = Snapshot(taskIds, request.RangeStart, request.RangeEnd, schedule.Operations);
        var token = NewToken();
        var now = clock.GetUtcNow();
        var expiresAt = now.Add(PlanLifetime);
        var plan = new AiPlanView(Guid.NewGuid(), expiresAt, snapshot, token, schedule.Operations,
            schedule.Conflicts, schedule.UnplannedTaskIds, schedule.Explanation);
        await ExecuteAsync(db, """
            INSERT INTO ai_plans(id, owner_id, status, snapshot_hash, confirmation_token_hash, operations, expires_at)
            VALUES(@id, @owner, 'validated', @snapshot, @token_hash, @operations, @expires)
            """, p =>
        {
            p.Add(new NpgsqlParameter("id", plan.PlanId));
            p.Add(new NpgsqlParameter("owner", ownerId));
            p.Add(new NpgsqlParameter("snapshot", plan.SnapshotHash));
            p.Add(new NpgsqlParameter("token_hash", Hash(token)));
            p.Add(new NpgsqlParameter("operations", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(plan.Operations) });
            p.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
        }, ct);
        await transaction.CommitAsync(ct);
        return new(PlanStatus.Created, Plan: plan);
    }

    public async Task<PlanResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid planId,
        ApplyPlanRequest request, CancellationToken ct)
    {
        var accepted = request.AcceptedOperationIds?.Distinct().ToList() ?? [];
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || planId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SnapshotHash) ||
            string.IsNullOrWhiteSpace(request.ConfirmationToken) ||
            accepted.Count is < 1 or > 20)
            return new(PlanStatus.Invalid, Code: "INVALID_PLAN");
        var hash = Hash("POST /ai/plans/" + planId + "/apply\n" + JsonSerializer.Serialize(request));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(PlanStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(PlanStatus.Replayed,
                    Applied: JsonSerializer.Deserialize<ApplyPlanResponse>(previous.Response))
                : new(PlanStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var plan = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition("""
            SELECT id AS "Id", status AS "Status", snapshot_hash AS "SnapshotHash",
                confirmation_token_hash AS "TokenHash", operations::text AS "OperationsJson",
                expires_at AS "ExpiresAt"
            FROM ai_plans WHERE id = @Id AND owner_id = @Owner
            """, new { Id = planId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
        if (plan is null) return new(PlanStatus.NotFound, Code: "PLAN_NOT_FOUND");
        var now = clock.GetUtcNow();
        if (plan.Status != "validated" || plan.ExpiresAt <= now ||
            plan.SnapshotHash != request.SnapshotHash ||
            plan.TokenHash != Hash(request.ConfirmationToken))
            return new(PlanStatus.Stale, Code: "PLAN_STALE");
        var operations = JsonSerializer.Deserialize<List<PlanOperationView>>(plan.OperationsJson) ?? [];
        var operationIds = operations.Select(x => x.OperationId).ToHashSet();
        if (operations.Count == 0 || !operationIds.SetEquals(accepted))
            return new(PlanStatus.Stale, Code: "PLAN_STALE");
        var operationTaskIds = operations.Select(x => x.TaskId).Distinct().ToArray();
        var versions = (await db.Database.GetDbConnection().QueryAsync<TaskVersionRow>(new CommandDefinition(
            "SELECT id AS \"Id\", version AS \"Version\" FROM tasks WHERE owner_id = @Owner AND deleted_at IS NULL AND id = ANY(@Ids)",
            new { Owner = ownerId, Ids = operationTaskIds },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct)))
            .ToDictionary(x => x.Id, x => x.Version);
        if (operations.Any(x => !versions.TryGetValue(x.TaskId, out var version) || version != x.ExpectedVersion))
            return new(PlanStatus.Stale, Code: "PLAN_STALE");

        var blockIds = new List<Guid>();
        foreach (var operation in operations)
        {
            var blockId = Guid.NewGuid();
            blockIds.Add(blockId);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO task_time_blocks(id, owner_id, task_id, start_at, end_at, version) VALUES({blockId}, {ownerId}, {operation.TaskId}, {operation.After.StartAt}, {operation.After.EndAt}, 1)", ct);
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ai_plans SET status='applied', applied_at={now} WHERE id={planId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        for (var index = 0; index < operations.Count; index++)
        {
            db.Changes.Add(new SyncChangeRow
            {
                OwnerId = ownerId,
                Revision = revision,
                ChangeIndex = index,
                EntityType = "time_block",
                EntityId = blockIds[index],
                EntityVersion = 1,
                Payload = JsonSerializer.Serialize(new
                {
                    id = blockIds[index],
                    task_id = operations[index].TaskId,
                    start_at = operations[index].After.StartAt,
                    end_at = operations[index].After.EndAt
                }),
                CreatedAt = now
            });
        }
        await ExecuteAsync(db, """
            INSERT INTO ai_action_audits(owner_id, plan_id, action_type, target_id, before_value, after_value, actor)
            VALUES(@owner, @plan, 'plan_apply', @target, NULL, @after, 'ai_confirmed')
            """, p =>
        {
            p.Add(new NpgsqlParameter("owner", ownerId));
            p.Add(new NpgsqlParameter("plan", planId));
            p.Add(new NpgsqlParameter("target", planId));
            p.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = plan.OperationsJson });
        }, ct);
        var applied = new ApplyPlanResponse("applied", operations.Select(x => x.OperationId).ToList());
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(applied),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PlanStatus.Applied, Applied: applied);
    }

    private static string Snapshot(IReadOnlyList<Guid> taskIds, DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<PlanOperationView> operations) =>
        Hash("plan\n" + string.Join(",", taskIds.OrderBy(x => x)) + "\n" +
             start.ToUniversalTime().ToString("O") + "\n" + end.ToUniversalTime().ToString("O") + "\n" +
             JsonSerializer.Serialize(operations));

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class BlockRow
    {
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    private sealed class PlanRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "validated";
        public string SnapshotHash { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public string OperationsJson { get; set; } = "[]";
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class TaskVersionRow
    {
        public Guid Id { get; set; }
        public long Version { get; set; }
    }
}
