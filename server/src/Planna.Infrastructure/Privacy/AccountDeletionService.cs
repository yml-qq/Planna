using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Identity;
using Planna.Application.Privacy;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Privacy;

// Final deletion confirmation: requires a fresh step-up token bound to this owner, disables the
// account, revokes every device session and records the accepted request in one transaction.
public sealed class AccountDeletionService(TaskWriteContext db, IStepUpService stepUp, TimeProvider clock)
    : IAccountDeletionService
{
    private const string PolicyVersion = "account-deletion-v1";

    public async Task<DeleteAccountResult> ExecuteAsync(Guid ownerId, Guid mutationId,
        DeleteAccountRequest request, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ConfirmationToken) ||
            !request.AcknowledgeOfflineDevices)
            return new(DeleteAccountStatus.Invalid, Code: "INVALID_DELETION_REQUEST");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST /me/deletion-requests\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"delete:" + ownerId.ToString("N")}, 0))", ct);
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(DeleteAccountStatus.Replayed,
                    JsonSerializer.Deserialize<DeleteAccountResponse>(previous.Response))
                : new(DeleteAccountStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1)
        {
            var exists = await db.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId}").SingleAsync(ct);
            return exists == 1
                ? new(DeleteAccountStatus.AlreadyDeleted, Code: "ACCOUNT_DELETED")
                : new(DeleteAccountStatus.Unavailable, Code: "ACCOUNT_NOT_FOUND");
        }
        if (!stepUp.TryValidateToken(ownerId, "account_delete", request.ConfirmationToken))
            return new(DeleteAccountStatus.StepUpRequired, Code: "STEP_UP_REQUIRED");

        var now = clock.GetUtcNow();
        await ExecuteAsync(db, "UPDATE users SET deleted_at=@Now WHERE id=@Owner AND deleted_at IS NULL", p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        await ExecuteAsync(db, """
            UPDATE device_sessions SET revoked_at=@Now, version=version+1
            WHERE user_id=@Owner AND revoked_at IS NULL
            """, p =>
        {
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            p.Add(new NpgsqlParameter("Owner", ownerId));
        }, ct);
        var requestId = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO account_deletion_requests(id, owner_id, status, policy_version, requested_at)
            VALUES(@Id, @Owner, 'accepted', @Policy, @Now)
            """, p =>
        {
            p.Add(new NpgsqlParameter("Id", requestId));
            p.Add(new NpgsqlParameter("Owner", ownerId));
            p.Add(new NpgsqlParameter("Policy", PolicyVersion));
            p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        }, ct);
        var response = new DeleteAccountResponse(requestId, "accepted", now, PolicyVersion);
        db.Mutations.Add(new MutationRow
        {
            OwnerId = ownerId,
            MutationId = mutationId,
            RequestHash = hash,
            Response = JsonSerializer.Serialize(response),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(DeleteAccountStatus.Accepted, response);
    }

    private static async Task ExecuteAsync(TaskWriteContext db, string sql,
        Action<DbParameterCollection> addParameters, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync(ct);
    }
}
