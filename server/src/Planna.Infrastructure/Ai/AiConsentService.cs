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

// Long-term AI data consent: a whitelisted scope set per owner (empty = revoked).
// Consent rows are not synced task data and never advance the owner revision.
public sealed class AiConsentService(TaskWriteContext db, TimeProvider clock) : IAiConsent
{
    public async Task<AiConsentResult> GetAsync(Guid ownerId, CancellationToken ct)
    {
        if (ownerId == Guid.Empty) return new(AiConsentStatus.Invalid, Code: "INVALID_REQUEST");
        var row = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<ConsentRow>(new CommandDefinition("""
            SELECT allowed_scopes AS "AllowedScopes", updated_at AS "UpdatedAt"
            FROM ai_consents WHERE owner_id = @Owner
            """, new { Owner = ownerId }, cancellationToken: ct));
        return new(AiConsentStatus.Ok, row is null
            ? new AiConsentView([], null)
            : new AiConsentView(row.AllowedScopes, Instant(row.UpdatedAt)));
    }

    public async Task<AiConsentResult> UpdateAsync(Guid ownerId, Guid mutationId, UpdateAiConsentRequest request,
        CancellationToken ct)
    {
        if (ownerId == Guid.Empty || mutationId == Guid.Empty || request.AllowedScopes is null ||
            request.AllowedScopes.Count > AiConsentScopes.Allowed.Count)
            return new(AiConsentStatus.Invalid, Code: "INVALID_SCOPE");
        var scopes = request.AllowedScopes.Distinct().ToList();
        if (scopes.Any(scope => !AiConsentScopes.IsKnown(scope)))
            return new(AiConsentStatus.Invalid, Code: "INVALID_SCOPE");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "PUT /ai/consent\n" + JsonSerializer.Serialize(request))));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"ai-consent:" + ownerId.ToString("N")}, 0))", ct);
        var active = await db.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM users WHERE id={ownerId} AND deleted_at IS NULL").SingleAsync(ct);
        if (active != 1) return new(AiConsentStatus.AccountDeleted, Code: "ACCOUNT_DELETED");
        var previous = await db.Mutations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.MutationId == mutationId, ct);
        if (previous is not null)
            return previous.RequestHash == hash
                ? new(AiConsentStatus.Replayed,
                    JsonSerializer.Deserialize<AiConsentView>(previous.Response))
                : new(AiConsentStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");

        var now = TruncateToMicroseconds(clock.GetUtcNow());
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                INSERT INTO ai_consents(owner_id, allowed_scopes, updated_at)
                VALUES(@Owner, @Scopes, @Now)
                ON CONFLICT (owner_id) DO UPDATE
                SET allowed_scopes = EXCLUDED.allowed_scopes, updated_at = EXCLUDED.updated_at
                """;
            command.Parameters.Add(new NpgsqlParameter("Owner", ownerId));
            command.Parameters.Add(new NpgsqlParameter("Scopes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = scopes.ToArray()
            });
            command.Parameters.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            await command.ExecuteNonQueryAsync(ct);
        }
        var view = new AiConsentView(scopes, now);
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
        return new(AiConsentStatus.Updated, view);
    }

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private sealed class ConsentRow
    {
        public string[] AllowedScopes { get; set; } = [];
        public DateTime UpdatedAt { get; set; }
    }
}
