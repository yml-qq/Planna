using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

// Single-occurrence edit (TIME-01F): reschedule due instant and skip/unskip one occurrence.
// Completion itself stays in TaskCompleteService; this command never targets done rows.
public sealed class OccurrencePatchService(TaskWriteContext db, TimeProvider clock) : IPatchOccurrence
{
    private static readonly string[] EditableStatuses = ["todo", "skipped"];

    public async Task<PatchOccurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        Guid occurrenceId, UpdateOccurrenceRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            occurrenceId == Guid.Empty || request.ExpectedVersion < 1 || request.Patch is null)
            return new(PatchOccurrenceStatus.Invalid, Code: "INVALID_OCCURRENCE");
        var requestedStatus = request.Patch.Status?.Trim().ToLowerInvariant();
        if (requestedStatus is not null && !EditableStatuses.Contains(requestedStatus))
            return new(PatchOccurrenceStatus.Invalid, Code: "INVALID_OCCURRENCE");
        if (request.Patch.DueAt is null && requestedStatus is null)
            return new(PatchOccurrenceStatus.Invalid, Code: "INVALID_OCCURRENCE");
        var hash = Hash("PATCH /tasks/" + taskId + "/occurrences/" + occurrenceId + "\n" +
                        JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM tasks WHERE id={taskId} AND owner_id={ownerId} AND deleted_at IS NULL")
            .SingleAsync(ct);
        if (task != 1) return new(PatchOccurrenceStatus.NotFound, Code: "TASK_NOT_FOUND");
        var previous = await ReplayAsync(ownerId, mutationId, hash, ct);
        if (previous is not null) return previous;
        var connection = db.Database.GetDbConnection();
        var dbTransaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var row = await connection.QuerySingleOrDefaultAsync<OccurrenceDbRow>(new CommandDefinition("""
            SELECT id AS "Id", status AS "Status", version AS "Version",
                original_local_start AS "OriginalLocalStart", time_zone AS "TimeZone",
                due_at AS "DueAt", completed_at AS "CompletedAt"
            FROM task_occurrences
            WHERE id=@Id AND owner_id=@Owner AND series_task_id=@Task
            FOR UPDATE
            """, new { Id = occurrenceId, Owner = ownerId, Task = taskId }, dbTransaction,
            cancellationToken: ct));
        if (row is null) return new(PatchOccurrenceStatus.NotFound, Code: "OCCURRENCE_NOT_FOUND");
        if (row.Version != request.ExpectedVersion)
            return new(PatchOccurrenceStatus.VersionConflict,
                Details: new VersionConflictDetails(occurrenceId, row.Version, []));
        if (row.Status == "done")
            return new(PatchOccurrenceStatus.Conflict, Code: "OCCURRENCE_COMPLETED");

        var now = clock.GetUtcNow();
        var status = requestedStatus ?? row.Status;
        var dueAt = request.Patch.DueAt?.ToUniversalTime() ?? AsInstant(row.DueAt);
        var version = row.Version + 1;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE task_occurrences SET status=@Status, due_at=@DueAt, version=@Version
            WHERE id=@Id AND owner_id=@Owner
            """, new
        {
            Status = status,
            DueAt = dueAt?.UtcDateTime,
            Version = version,
            Id = occurrenceId,
            Owner = ownerId
        }, dbTransaction, cancellationToken: ct));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var view = new OccurrenceView(occurrenceId, taskId, row.OriginalLocalStart, row.TimeZone, dueAt,
            status, AsInstant(row.CompletedAt), version);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "occurrence",
            EntityId = occurrenceId,
            EntityVersion = version,
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
        return new(PatchOccurrenceStatus.Updated, view);
    }

    private async Task<PatchOccurrenceResult?> ReplayAsync(Guid ownerId, Guid mutationId, string hash,
        CancellationToken ct)
    {
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is null) return null;
        if (previous.RequestHash != hash)
            return new(PatchOccurrenceStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        using var stored = JsonDocument.Parse(previous.Response);
        var view = JsonSerializer.Deserialize<OccurrenceView>(previous.Response);
        return view is null ? new(PatchOccurrenceStatus.Replayed) : new(PatchOccurrenceStatus.Replayed, view);
    }

    private static DateTimeOffset? AsInstant(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class OccurrenceDbRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "todo";
        public long Version { get; set; }
        public DateTime OriginalLocalStart { get; set; }
        public string TimeZone { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
