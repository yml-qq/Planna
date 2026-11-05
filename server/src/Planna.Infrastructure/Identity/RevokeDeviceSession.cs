using Microsoft.EntityFrameworkCore;
using Planna.Application.Identity;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

// EF Core write model. The row lock makes version checks and replay handling atomic.
public sealed class RevokeDeviceSession(AuthContext db, TimeProvider clock) : IRevokeDeviceSession
{
    public async Task<RevokeDeviceStatus> ExecuteAsync(Guid ownerId, Guid sessionId,
        int expectedVersion, Guid requestKey, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE device_sessions SET version=version
            WHERE id={sessionId} AND user_id={ownerId}
                AND EXISTS (SELECT 1 FROM users WHERE id={ownerId} AND deleted_at IS NULL)
            """, ct);
        if (locked == 0) return RevokeDeviceStatus.NotFound;

        var session = await db.Sessions.SingleAsync(x => x.Id == sessionId && x.UserId == ownerId, ct);
        if (session.RevokedAt is not null)
            return session.RevocationKey == requestKey
                ? RevokeDeviceStatus.Replayed : RevokeDeviceStatus.VersionConflict;
        if (session.Version != expectedVersion)
            return RevokeDeviceStatus.VersionConflict;

        session.RevokedAt = clock.GetUtcNow();
        session.RevocationKey = requestKey;
        session.Version++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return RevokeDeviceStatus.Revoked;
    }
}
