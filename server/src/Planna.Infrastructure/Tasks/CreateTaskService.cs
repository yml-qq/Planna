using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class CreateTaskService(TaskWriteContext db, TimeProvider clock) : ICreateTask
{
    public async Task<CreateTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateTaskRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || request.Id == Guid.Empty || request.ListId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 500 ||
            (request.Description?.Length ?? 0) > 20000 || request.Priority is < 0 or > 3 ||
            request.EstimateMinutes is < 1 or > 1440 ||
            request.AllDay && request.DueAt is not null || !request.AllDay && request.DueLocalDate is not null ||
            request.DueAt < request.StartAt ||
            string.IsNullOrWhiteSpace(request.TimeZone ?? "Asia/Shanghai"))
            return new(CreateTaskStatus.Invalid, Code: "INVALID_TASK");

        var zone = request.TimeZone ?? "Asia/Shanghai";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (TimeZoneNotFoundException) { return new(CreateTaskStatus.Invalid, Code: "INVALID_TIME_ZONE"); }
        catch (InvalidTimeZoneException) { return new(CreateTaskStatus.Invalid, Code: "INVALID_TIME_ZONE"); }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST /tasks\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // This no-op update locks the owner clock until commit and also rejects deleted accounts.
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(CreateTaskStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(CreateTaskStatus.Replayed, JsonSerializer.Deserialize<TaskResponse>(previous.Response))
                : new(CreateTaskStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var listExists = await db.Lists.AsNoTracking()
            .AnyAsync(x => x.Id == request.ListId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (!listExists) return new(CreateTaskStatus.ListNotFound, Code: "LIST_NOT_FOUND");

        var now = clock.GetUtcNow();
        var row = new TaskRow
        {
            Id = request.Id,
            OwnerId = ownerId,
            ListId = request.ListId,
            Title = request.Title.Trim(),
            Description = request.Description ?? "",
            Priority = request.Priority,
            AllDay = request.AllDay,
            StartAt = request.StartAt?.ToUniversalTime(),
            DueAt = request.DueAt?.ToUniversalTime(),
            DueLocalDate = request.DueLocalDate,
            TimeZone = zone,
            EstimateMinutes = request.EstimateMinutes,
            CreatedAt = now,
            UpdatedAt = now
        };
        var response = new TaskResponse(row.Id, row.ListId, row.Title, row.Description, row.Status,
            row.Priority, row.StartAt, row.DueAt, row.DueLocalDate, row.AllDay, row.TimeZone,
            row.EstimateMinutes, row.Version);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        db.Tasks.Add(row);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityId = row.Id,
            EntityVersion = 1,
            Payload = JsonSerializer.Serialize(response),
            CreatedAt = now
        });
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(response),
            CreatedAt = now
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return new(CreateTaskStatus.Conflict, Code: "TASK_ID_CONFLICT");
        }
        await transaction.CommitAsync(ct);
        return new(CreateTaskStatus.Created, response);
    }
}
