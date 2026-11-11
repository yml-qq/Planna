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
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Ai;

public sealed class BreakdownService(TaskWriteContext db, TimeProvider clock, IAiBreakdownProvider provider)
    : ICreateBreakdown, IEditBreakdown, IApplyBreakdown
{
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromHours(1);

    public async Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateBreakdownRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            request.ExpectedVersion < 1 || (request.Guidance?.Length ?? 0) > 2000)
            return new(BreakdownStatus.Invalid, Code: "INVALID_BREAKDOWN");
        var hash = Hash("POST /ai/tasks/" + taskId + "/breakdowns\n" + JsonSerializer.Serialize(request));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(BreakdownStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;

        var task = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (task is null) return new(BreakdownStatus.NotFound, Code: "TASK_NOT_FOUND");
        if (task.Version != request.ExpectedVersion)
            return new(BreakdownStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, task.Version, []));

        var steps = provider.Generate(task.Title, request.Guidance);
        if (!ValidSteps(steps, out var normalized)) return new(BreakdownStatus.Invalid, Code: "INVALID_BREAKDOWN");
        var now = clock.GetUtcNow();
        var expiresAt = now.Add(DraftLifetime);
        var token = NewToken();
        var draft = new AiBreakdownDraftView(Guid.NewGuid(), taskId, task.Version, expiresAt,
            Snapshot(normalized, task.Version), token, normalized, []);

        await ExecuteAsync(db, """
            INSERT INTO ai_breakdown_drafts(id, owner_id, task_id, task_version, status, snapshot_hash,
                steps, assumptions, confirmation_token_hash, expires_at)
            VALUES(@id, @owner, @task, @version, 'awaiting_confirmation', @snapshot,
                @steps, '[]'::jsonb, @token_hash, @expires)
            """, p =>
        {
            p.Add(new NpgsqlParameter("id", draft.DraftId));
            p.Add(new NpgsqlParameter("owner", ownerId));
            p.Add(new NpgsqlParameter("task", taskId));
            p.Add(new NpgsqlParameter("version", task.Version));
            p.Add(new NpgsqlParameter("snapshot", draft.SnapshotHash));
            p.Add(new NpgsqlParameter("steps", NpgsqlDbType.Jsonb) { Value = StepsJson(normalized) });
            p.Add(new NpgsqlParameter("token_hash", TokenHash(token)));
            p.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
        }, ct);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, draft, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(BreakdownStatus.Created, Draft: draft);
    }

    public async Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid draftId,
        EditBreakdownRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || draftId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SnapshotHash) ||
            !ValidSteps(request.Steps ?? [], out var normalized))
            return new(BreakdownStatus.Invalid, Code: "INVALID_BREAKDOWN");
        var hash = Hash("PUT /ai/breakdowns/" + draftId + "\n" + JsonSerializer.Serialize(request));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;
        var draft = await LoadDraftAsync(ownerId, draftId, ct);
        if (draft is null) return new(BreakdownStatus.NotFound, Code: "BREAKDOWN_NOT_FOUND");
        if (draft.Status != "awaiting_confirmation" || draft.SnapshotHash != request.SnapshotHash)
            return new(BreakdownStatus.Stale, Code: "BREAKDOWN_STALE");

        var token = NewToken();
        var snapshot = Snapshot(normalized, draft.TaskVersion);
        await ExecuteAsync(db, """
            UPDATE ai_breakdown_drafts SET steps=@steps, snapshot_hash=@snapshot, confirmation_token_hash=@token_hash
            WHERE id=@id
            """, p =>
        {
            p.Add(new NpgsqlParameter("id", draftId));
            p.Add(new NpgsqlParameter("steps", NpgsqlDbType.Jsonb) { Value = StepsJson(normalized) });
            p.Add(new NpgsqlParameter("snapshot", snapshot));
            p.Add(new NpgsqlParameter("token_hash", TokenHash(token)));
        }, ct);
        var view = new AiBreakdownDraftView(draft.Id, draft.TaskId, draft.TaskVersion, draft.ExpiresAt,
            snapshot, token, normalized, []);
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, view, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(BreakdownStatus.Edited, Draft: view);
    }

    public async Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid draftId,
        ApplyBreakdownRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || draftId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SnapshotHash) || string.IsNullOrWhiteSpace(request.ConfirmationToken))
            return new(BreakdownStatus.Invalid, Code: "INVALID_BREAKDOWN");
        var hash = Hash("POST /ai/breakdowns/" + draftId + "/apply\n" + JsonSerializer.Serialize(request));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(BreakdownStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var replay = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (replay is not null) return replay;

        var draft = await LoadDraftAsync(ownerId, draftId, ct);
        if (draft is null) return new(BreakdownStatus.NotFound, Code: "BREAKDOWN_NOT_FOUND");
        var now = clock.GetUtcNow();
        if (draft.Status != "awaiting_confirmation" || draft.ExpiresAt <= now ||
            draft.SnapshotHash != request.SnapshotHash ||
            draft.TokenHash != TokenHash(request.ConfirmationToken))
            return new(BreakdownStatus.Stale, Code: "BREAKDOWN_STALE");
        var task = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == draft.TaskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (task is null || task.Version != draft.TaskVersion)
            return new(BreakdownStatus.Stale, Code: "BREAKDOWN_STALE");
        var steps = ReadSteps(draft.StepsJson);
        if (steps.Count == 0) return new(BreakdownStatus.Stale, Code: "BREAKDOWN_STALE");
        var conflict = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM task_steps WHERE owner_id={ownerId} AND id = ANY({steps.Select(x => x.StepId).ToArray()})").SingleAsync(ct);
        if (conflict > 0) return new(BreakdownStatus.StepConflict, Code: "STEP_ID_CONFLICT");

        foreach (var step in steps)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO task_steps(id, owner_id, task_id, title, position, estimate_minutes, version) VALUES({step.StepId}, {ownerId}, {draft.TaskId}, {step.Title}, {(decimal)step.Position}, {step.EstimateMinutes}, 1)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ai_breakdown_drafts SET status='applied', applied_at={now} WHERE id={draftId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        for (var index = 0; index < steps.Count; index++)
        {
            db.Changes.Add(new SyncChangeRow
            {
                OwnerId = ownerId,
                Revision = revision,
                ChangeIndex = index,
                EntityType = "step",
                EntityId = steps[index].StepId,
                EntityVersion = 1,
                Payload = JsonSerializer.Serialize(steps[index]),
                CreatedAt = now
            });
        }
        var audit = JsonSerializer.Serialize(steps.Select(x => new { id = x.StepId, title = x.Title, x.Position }));
        await ExecuteAsync(db, """
            INSERT INTO ai_action_audits(owner_id, action_type, target_id, before_value, after_value, actor)
            VALUES(@owner, 'breakdown_apply', @target, NULL, @after, 'ai_confirmed')
            """, p =>
        {
            p.Add(new NpgsqlParameter("owner", ownerId));
            p.Add(new NpgsqlParameter("target", draft.TaskId));
            p.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = audit });
        }, ct);
        var applied = new ApplyBreakdownResponse("applied", draft.TaskId, steps.Select(x => x.StepId).ToList());
        db.Mutations.Add(Mutation(ownerId, mutationId, hash, applied, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(BreakdownStatus.Applied, Applied: applied);
    }

    private async Task<BreakdownResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash, CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash) return new(BreakdownStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        return stored.RootElement.TryGetProperty("status", out _) &&
               stored.RootElement.GetProperty("status").GetString() == "applied"
            ? new(BreakdownStatus.Replayed, Applied: JsonSerializer.Deserialize<ApplyBreakdownResponse>(previous.Response))
            : new(BreakdownStatus.Replayed, Draft: JsonSerializer.Deserialize<AiBreakdownDraftView>(previous.Response));
    }

    private async Task<DraftRow?> LoadDraftAsync(Guid ownerId, Guid draftId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        return await connection.QuerySingleOrDefaultAsync<DraftRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", task_version AS "TaskVersion", status AS "Status",
                snapshot_hash AS "SnapshotHash", steps::text AS "StepsJson",
                confirmation_token_hash AS "TokenHash", expires_at AS "ExpiresAt"
            FROM ai_breakdown_drafts WHERE id=@Id AND owner_id=@Owner
            """, new { Id = draftId, Owner = ownerId },
            db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: ct));
    }

    private static List<BreakdownStepView> ReadSteps(string json) =>
        JsonSerializer.Deserialize<List<BreakdownStepView>>(json) ?? [];

    private static bool ValidSteps(IReadOnlyList<BreakdownStepView> steps, out List<BreakdownStepView> normalized)
    {
        normalized = [];
        if (steps is null || steps.Count is < 1 or > 20) return false;
        var ids = new HashSet<Guid>();
        var positions = new HashSet<int>();
        foreach (var step in steps)
        {
            var title = step.Title?.Trim() ?? "";
            if (step.StepId == Guid.Empty || title.Length is < 1 or > 500 ||
                step.EstimateMinutes is < 1 or > 1440 ||
                step.Position is < 0 or > 19 ||
                !ids.Add(step.StepId) || !positions.Add(step.Position))
                return false;
            normalized.Add(new BreakdownStepView(step.StepId, title, step.EstimateMinutes, step.Position));
        }
        normalized = normalized.OrderBy(x => x.Position).ToList();
        return true;
    }

    private static string StepsJson(IReadOnlyList<BreakdownStepView> steps) =>
        JsonSerializer.Serialize(steps.OrderBy(x => x.Position));

    private static string Snapshot(IReadOnlyList<BreakdownStepView> steps, long taskVersion) =>
        Hash("snapshot\n" + taskVersion + "\n" + StepsJson(steps));

    private static string TokenHash(string token) => Hash(token);

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MutationRow Mutation(Guid ownerId, Guid mutationId, string hash, object response, DateTimeOffset now) => new()
    {
        OwnerId = ownerId,
        MutationId = mutationId,
        RequestHash = hash,
        Response = JsonSerializer.Serialize(response),
        CreatedAt = now
    };

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class DraftRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public long TaskVersion { get; set; }
        public string Status { get; set; } = "awaiting_confirmation";
        public string SnapshotHash { get; set; } = "";
        public string StepsJson { get; set; } = "[]";
        public string TokenHash { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }
}
