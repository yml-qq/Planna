using System.Security.Cryptography;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Planna.Application.Identity;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

// Drains the step-up email outbox: leases one job, decrypts the bound code and sends it.
// Mirrors the login dispatcher's retry/lease semantics without touching login rate limits.
public sealed class StepUpEmailDispatcher(
    AuthContext db, ILoginCodeSender sender, LoginCodeCipher cipher,
    TimeProvider clock, ILogger<StepUpEmailDispatcher> logger)
{
    private const int MaxAttempts = 5;

    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE auth_step_up_email_outbox AS o
            SET status='expired', ciphertext=NULL, nonce=NULL, tag=NULL, lease_token=NULL, lease_until=NULL
            FROM auth_step_up_challenges AS c
            WHERE o.challenge_id=c.id AND o.status IN ('pending','leased')
              AND (c.expires_at<={now} OR c.consumed_at IS NOT NULL)
            """, ct);

        var connection = db.Database.GetDbConnection();
        JobRow? job;
        Guid leaseToken;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            job = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
                SELECT o.id AS "Id", o.challenge_id AS "ChallengeId", o.ciphertext AS "Ciphertext",
                    o.nonce AS "Nonce", o.tag AS "Tag", o.attempts AS "Attempts",
                    c.expires_at AS "ExpiresAt", u.email AS "Email"
                FROM auth_step_up_email_outbox AS o
                JOIN auth_step_up_challenges AS c ON c.id=o.challenge_id
                JOIN users AS u ON u.id=c.user_id
                WHERE ((o.status='pending' AND o.next_attempt_at<=@Now) OR
                       (o.status='leased' AND o.lease_until<=@Now))
                  AND c.expires_at>@Now AND c.consumed_at IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM auth_step_up_challenges AS newer
                    WHERE newer.user_id=c.user_id AND
                      (newer.created_at>c.created_at OR
                       (newer.created_at=c.created_at AND newer.id>c.id)))
                ORDER BY o.next_attempt_at, o.created_at
                LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                """, new { Now = now.UtcDateTime }, transaction.GetDbTransaction(), cancellationToken: ct));
            if (job is null) return false;
            leaseToken = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE auth_step_up_email_outbox
                SET status='leased', lease_token=@Lease, lease_until=@Until
                WHERE id=@Id
                """, new { Id = job.Id, Lease = leaseToken, Until = now.AddMinutes(1).UtcDateTime },
                transaction.GetDbTransaction(), cancellationToken: ct));
            await transaction.CommitAsync(ct);
        }

        try
        {
            var code = cipher.Decrypt(job.ChallengeId, job.Email, job.Ciphertext!, job.Nonce!, job.Tag!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await sender.SendAsync(job.Email, code, timeout.Token);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE auth_step_up_email_outbox
                SET status='sent', ciphertext=NULL, nonce=NULL, tag=NULL, lease_token=NULL, lease_until=NULL,
                    sent_at=@Now
                WHERE id=@Id AND lease_token=@Lease AND status='leased'
                """, new { Id = job.Id, Lease = leaseToken, Now = clock.GetUtcNow().UtcDateTime },
                cancellationToken: ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempts = job.Attempts + 1;
            var terminal = ex is CryptographicException || attempts >= MaxAttempts ||
                clock.GetUtcNow() >= Instant(job.ExpiresAt);
            var nextAttempt = clock.GetUtcNow().AddSeconds(Math.Min(60, 1 << attempts));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE auth_step_up_email_outbox
                SET status=@Status, attempts=@Attempts, next_attempt_at=@Next,
                    ciphertext=@Ciphertext, nonce=@Nonce, tag=@Tag, lease_token=NULL, lease_until=NULL
                WHERE id=@Id AND lease_token=@Lease AND status='leased'
                """, new
            {
                Id = job.Id,
                Lease = leaseToken,
                Status = terminal ? "failed" : "pending",
                Attempts = attempts,
                Next = nextAttempt.UtcDateTime,
                Ciphertext = terminal ? null : job.Ciphertext,
                Nonce = terminal ? null : job.Nonce,
                Tag = terminal ? null : job.Tag
            }, cancellationToken: ct));
            if (terminal)
                logger.LogError("STEP_UP_EMAIL_DELIVERY_TERMINAL challenge_id={ChallengeId} attempts={Attempts}",
                    job.ChallengeId, attempts);
            else
                logger.LogWarning("STEP_UP_EMAIL_DELIVERY_RETRY challenge_id={ChallengeId} attempt={Attempt}",
                    job.ChallengeId, attempts);
        }
        return true;
    }

    private static DateTimeOffset Instant(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class JobRow
    {
        public Guid Id { get; set; }
        public Guid ChallengeId { get; set; }
        public byte[]? Ciphertext { get; set; }
        public byte[]? Nonce { get; set; }
        public byte[]? Tag { get; set; }
        public int Attempts { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Email { get; set; } = "";
    }
}
