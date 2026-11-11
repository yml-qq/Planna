using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Tasks;

public sealed class RecurrenceService(TaskWriteContext db, TimeProvider clock) : IPutRecurrence, IDeleteRecurrence
{
    public async Task<PutRecurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        PutRecurrenceRequest request, CancellationToken ct)
    {
        if (request.Kind is not ("rrule" or "after_completion"))
            return new(PutRecurrenceStatus.Unsupported, Code: "UNSUPPORTED_RECURRENCE");
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty ||
            request.ExpectedVersion < 0 || string.IsNullOrWhiteSpace(request.TimeZone) ||
            !RecurrenceFormats.TryParseLocal(request.DtstartLocal, out var dtstartLocal))
            return new(PutRecurrenceStatus.Invalid, Code: "INVALID_RECURRENCE");
        if (request.Kind == "rrule")
        {
            if (string.IsNullOrWhiteSpace(request.Rrule) || request.IntervalDays is not null ||
                !RecurrenceExpander.TryParse(request.Rrule, out _))
                return new(PutRecurrenceStatus.Invalid, Code: "INVALID_RECURRENCE");
        }
        else
        {
            if (request.IntervalDays is not (>= 1 and <= 3650) || !string.IsNullOrWhiteSpace(request.Rrule))
                return new(PutRecurrenceStatus.Invalid, Code: "INVALID_RECURRENCE");
        }
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone); }
        catch (TimeZoneNotFoundException) { return new(PutRecurrenceStatus.Invalid, Code: "INVALID_TIME_ZONE"); }
        catch (InvalidTimeZoneException) { return new(PutRecurrenceStatus.Invalid, Code: "INVALID_TIME_ZONE"); }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PUT /tasks/" + taskId + "/recurrence\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(PutRecurrenceStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(PutRecurrenceStatus.Replayed, JsonSerializer.Deserialize<RecurrenceView>(previous.Response))
                : new(PutRecurrenceStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var taskExists = await db.Tasks.AsNoTracking()
            .AnyAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (!taskExists) return new(PutRecurrenceStatus.TaskNotFound, Code: "TASK_NOT_FOUND");
        var existingVersion = await db.Database.SqlQuery<long>(
            $"SELECT version AS \"Value\" FROM recurrences WHERE owner_id={ownerId} AND task_id={taskId}").FirstOrDefaultAsync(ct);

        long newVersion;
        if (request.ExpectedVersion == 0)
        {
            if (existingVersion > 0) return new(PutRecurrenceStatus.Exists, Code: "RECURRENCE_EXISTS");
            await ExecuteTimestampSql(db, """
                INSERT INTO recurrences(owner_id, task_id, kind, rrule, interval_days, dtstart_local, time_zone)
                VALUES(@owner, @task, @kind, @rrule, @interval, @dtstart, @tz)
                """, p =>
            {
                p.Add(new NpgsqlParameter("owner", ownerId));
                p.Add(new NpgsqlParameter("task", taskId));
                p.Add(new NpgsqlParameter("kind", request.Kind));
                p.Add(new NpgsqlParameter("rrule", (object?)request.Rrule ?? DBNull.Value));
                p.Add(new NpgsqlParameter("interval", (object?)request.IntervalDays ?? DBNull.Value));
                p.Add(new NpgsqlParameter("dtstart", NpgsqlDbType.Timestamp) { Value = dtstartLocal });
                p.Add(new NpgsqlParameter("tz", request.TimeZone));
            }, ct);
            newVersion = 1;
        }
        else
        {
            if (existingVersion == 0) return new(PutRecurrenceStatus.NotFound, Code: "RECURRENCE_NOT_FOUND");
            if (existingVersion != request.ExpectedVersion)
                return new(PutRecurrenceStatus.VersionConflict,
                    Details: new VersionConflictDetails(taskId, existingVersion, []));
            newVersion = existingVersion + 1;
            await ExecuteTimestampSql(db, """
                UPDATE recurrences SET kind=@kind, rrule=@rrule, interval_days=@interval,
                    dtstart_local=@dtstart, time_zone=@tz,
                    version=@version, updated_at=now() WHERE owner_id=@owner AND task_id=@task
                """, p =>
            {
                p.Add(new NpgsqlParameter("owner", ownerId));
                p.Add(new NpgsqlParameter("task", taskId));
                p.Add(new NpgsqlParameter("kind", request.Kind));
                p.Add(new NpgsqlParameter("rrule", (object?)request.Rrule ?? DBNull.Value));
                p.Add(new NpgsqlParameter("interval", (object?)request.IntervalDays ?? DBNull.Value));
                p.Add(new NpgsqlParameter("dtstart", NpgsqlDbType.Timestamp) { Value = dtstartLocal });
                p.Add(new NpgsqlParameter("tz", request.TimeZone));
                p.Add(new NpgsqlParameter("version", newVersion));
            }, ct);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var now = clock.GetUtcNow();
        var view = new RecurrenceView(taskId, request.Kind, request.Rrule, request.IntervalDays,
            request.TimeZone, dtstartLocal, newVersion);
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "recurrence",
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
        return new(PutRecurrenceStatus.Saved, view);
    }

    public async Task<DeleteRecurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        long expectedVersion, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || taskId == Guid.Empty || expectedVersion < 1)
            return new(DeleteRecurrenceStatus.Invalid);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "DELETE /tasks/" + taskId + "/recurrence\n" + expectedVersion)));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var active = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks AS c SET revision=c.revision WHERE c.owner_id={ownerId} AND EXISTS (SELECT 1 FROM users AS u WHERE u.id=c.owner_id AND u.deleted_at IS NULL)", ct);
        if (active != 1) return new(DeleteRecurrenceStatus.AccountDeleted, Code: "ACCOUNT_DELETED");

        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(DeleteRecurrenceStatus.Replayed)
                : new(DeleteRecurrenceStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var taskExists = await db.Tasks.AsNoTracking()
            .AnyAsync(x => x.Id == taskId && x.OwnerId == ownerId && x.DeletedAt == null, ct);
        if (!taskExists) return new(DeleteRecurrenceStatus.TaskNotFound, Code: "TASK_NOT_FOUND");
        var existingVersion = await db.Database.SqlQuery<long>(
            $"SELECT version AS \"Value\" FROM recurrences WHERE owner_id={ownerId} AND task_id={taskId}").FirstOrDefaultAsync(ct);
        if (existingVersion == 0) return new(DeleteRecurrenceStatus.NotFound, Code: "RECURRENCE_NOT_FOUND");
        if (existingVersion != expectedVersion)
            return new(DeleteRecurrenceStatus.VersionConflict,
                Details: new VersionConflictDetails(taskId, existingVersion, []));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM recurrences WHERE owner_id={ownerId} AND task_id={taskId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE owner_sync_clocks SET revision=revision+1 WHERE owner_id={ownerId}", ct);
        var revision = await db.Database.SqlQuery<long>(
            $"SELECT revision AS \"Value\" FROM owner_sync_clocks WHERE owner_id={ownerId}").SingleAsync(ct);
        var now = clock.GetUtcNow();
        var tombstone = JsonSerializer.Serialize(new { task_id = taskId, version = expectedVersion + 1 });
        db.Changes.Add(new SyncChangeRow
        {
            OwnerId = ownerId,
            Revision = revision,
            EntityType = "recurrence",
            EntityId = taskId,
            Op = "delete",
            EntityVersion = expectedVersion + 1,
            Payload = tombstone,
            CreatedAt = now
        });
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = "{}",
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(DeleteRecurrenceStatus.Deleted);
    }

    private static async Task ExecuteTimestampSql(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }
}
