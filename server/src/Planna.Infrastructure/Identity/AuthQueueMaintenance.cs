using Microsoft.EntityFrameworkCore;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

public sealed record AuthQueueSnapshot(
    int Pending, int Leased, int Failed, long OldestWaitingSeconds,
    int DeletedChallenges, int DeletedRevokedTokens)
{
    public bool BacklogAlert => Pending + Leased >= 100 || OldestWaitingSeconds >= 120;
}

public sealed class AuthQueueMaintenance(AuthContext db, TimeProvider clock)
{
    private const int BatchSize = 500;

    public async Task<AuthQueueSnapshot> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var challengeCutoff = now.AddHours(-24);
        var revokedCutoff = now.AddDays(-30);

        // Deleting a challenge cascades to its email job and removes any remaining encrypted code.
        var deletedChallenges = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM auth_login_challenges
            WHERE id IN (
                SELECT id FROM auth_login_challenges
                WHERE created_at < {challengeCutoff} AND expires_at <= {now}
                ORDER BY created_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
            """, ct);
        // Active sessions keep every token hash so an old token replay still revokes the session.
        var deletedTokens = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM refresh_token_history
            WHERE token_hash IN (
                SELECT h.token_hash FROM refresh_token_history AS h
                JOIN device_sessions AS s ON s.id=h.session_id
                WHERE s.revoked_at IS NOT NULL AND s.revoked_at < {revokedCutoff}
                ORDER BY h.created_at LIMIT {BatchSize} FOR UPDATE OF h SKIP LOCKED)
            """, ct);

        var waiting = await db.EmailOutbox.AsNoTracking()
            .Where(x => x.Status == "pending" || x.Status == "leased")
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Pending = g.Count(x => x.Status == "pending"),
                Leased = g.Count(x => x.Status == "leased"),
                Oldest = g.Min(x => x.CreatedAt)
            }).SingleOrDefaultAsync(ct);
        var failed = await db.EmailOutbox.AsNoTracking().CountAsync(x => x.Status == "failed", ct);
        var oldestSeconds = waiting is null ? 0 :
            Math.Max(0, (long)(now - waiting.Oldest).TotalSeconds);
        return new AuthQueueSnapshot(waiting?.Pending ?? 0, waiting?.Leased ?? 0,
            failed, oldestSeconds, deletedChallenges, deletedTokens);
    }
}
