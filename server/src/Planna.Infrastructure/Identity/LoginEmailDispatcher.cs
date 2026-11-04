using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Planna.Application.Identity;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

public sealed class LoginEmailDispatcher(
    AuthContext db, ILoginCodeSender sender, LoginCodeCipher cipher,
    TimeProvider clock, ILogger<LoginEmailDispatcher> logger)
{
    private const int MaxAttempts = 5;

    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Clear encrypted codes that can no longer produce a valid login.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE auth_email_outbox AS o
            SET status='expired', ciphertext=NULL, nonce=NULL, tag=NULL,
                lease_token=NULL, lease_until=NULL
            FROM auth_login_challenges AS c
            WHERE o.challenge_id=c.id AND o.status IN ('pending','leased')
              AND (c.expires_at<={now} OR c.consumed_at IS NOT NULL OR EXISTS (
                SELECT 1 FROM auth_login_challenges AS newer
                WHERE newer.email=c.email AND
                  (newer.created_at>c.created_at OR
                   (newer.created_at=c.created_at AND newer.id>c.id))))
            """, ct);

        LoginEmailOutboxRow? job;
        Guid leaseToken;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var due = await db.EmailOutbox.FromSqlInterpolated($"""
                SELECT o.* FROM auth_email_outbox AS o
                JOIN auth_login_challenges AS c ON c.id=o.challenge_id
                WHERE ((o.status='pending' AND o.next_attempt_at<={now}) OR
                       (o.status='leased' AND o.lease_until<={now}))
                  AND c.expires_at>{now} AND c.consumed_at IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM auth_login_challenges AS newer
                    WHERE newer.email=c.email AND
                      (newer.created_at>c.created_at OR
                       (newer.created_at=c.created_at AND newer.id>c.id)))
                ORDER BY o.next_attempt_at, o.created_at
                LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                """).ToListAsync(ct);
            job = due.SingleOrDefault();
            if (job is null) return false;
            leaseToken = Guid.NewGuid();
            job.Status = "leased";
            job.LeaseToken = leaseToken;
            job.LeaseUntil = now.AddMinutes(1);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var challenge = await db.Challenges.AsNoTracking()
            .SingleAsync(x => x.Id == job.ChallengeId, ct);
        try
        {
            var code = cipher.Decrypt(job.ChallengeId, challenge.Email,
                job.Ciphertext!, job.Nonce!, job.Tag!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await sender.SendAsync(challenge.Email, code, timeout.Token);
            await db.EmailOutbox.Where(x => x.ChallengeId == job.ChallengeId &&
                x.LeaseToken == leaseToken && x.Status == "leased")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "sent")
                    .SetProperty(x => x.Ciphertext, (byte[]?)null)
                    .SetProperty(x => x.Nonce, (byte[]?)null)
                    .SetProperty(x => x.Tag, (byte[]?)null)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.SentAt, clock.GetUtcNow()), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempts = job.Attempts + 1;
            var terminal = ex is CryptographicException || attempts >= MaxAttempts ||
                clock.GetUtcNow() >= challenge.ExpiresAt;
            var nextAttempt = clock.GetUtcNow().AddSeconds(Math.Min(60, 1 << attempts));
            await db.EmailOutbox.Where(x => x.ChallengeId == job.ChallengeId &&
                x.LeaseToken == leaseToken && x.Status == "leased")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, terminal ? "failed" : "pending")
                    .SetProperty(x => x.Attempts, attempts)
                    .SetProperty(x => x.NextAttemptAt, nextAttempt)
                    .SetProperty(x => x.Ciphertext, terminal ? null : job.Ciphertext)
                    .SetProperty(x => x.Nonce, terminal ? null : job.Nonce)
                    .SetProperty(x => x.Tag, terminal ? null : job.Tag)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
            if (terminal)
                logger.LogError("AUTH_EMAIL_DELIVERY_TERMINAL challenge_id={ChallengeId} attempts={Attempts}",
                    job.ChallengeId, attempts);
            else
                logger.LogWarning("AUTH_EMAIL_DELIVERY_RETRY challenge_id={ChallengeId} attempt={Attempt}",
                    job.ChallengeId, attempts);
        }
        return true;
    }
}
