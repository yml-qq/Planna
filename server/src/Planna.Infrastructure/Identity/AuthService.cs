using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Planna.Application.Identity;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

public sealed class AuthService(
    AuthContext db, LoginCodeCipher cipher, IAccessTokenIssuer issuer,
    TimeProvider clock, string codePepper) : IAuthService
{
    private const int AccessSeconds = 600;
    private const int ChallengeWindowMinutes = 15;
    private const int LoginFailureWindowMinutes = 15;
    private readonly byte[] rateKey = Encoding.UTF8.GetBytes(codePepper);

    public async Task<AuthResult> ChallengeAsync(string email, Guid requestKey, Guid deviceId,
        IPAddress? remoteAddress, CancellationToken ct)
    {
        var normalized = Normalize(email);
        if (normalized is null || requestKey == Guid.Empty || deviceId == Guid.Empty)
            return new(AuthStatus.Invalid);
        var ipHash = RateHash("ip", NormalizeAddress(remoteAddress));
        var deviceHash = RateHash("device", deviceId.ToString("N"));
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({normalized}, 0))", ct);
        if (await db.Challenges.AnyAsync(x => x.Email == normalized && x.RequestKey == requestKey, ct))
            return new(AuthStatus.Accepted);
        var since = now.AddMinutes(-ChallengeWindowMinutes);
        var emailWindow = db.Challenges.Where(x => x.Email == normalized && x.CreatedAt > since);
        if (await emailWindow.CountAsync(ct) >= 3)
            return RateLimited(await emailWindow.MinAsync(x => x.CreatedAt, ct), now, ChallengeWindowMinutes);

        // The locks serialize the limits across all API instances before counting or inserting.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"auth-ip:" + ipHash}, 0))", ct);
        var ipWindow = db.Challenges.Where(x => x.IpHash == ipHash && x.CreatedAt > since);
        if (await ipWindow.CountAsync(ct) >= 20)
            return RateLimited(await ipWindow.MinAsync(x => x.CreatedAt, ct), now, ChallengeWindowMinutes);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"auth-device:" + deviceHash}, 0))", ct);
        var deviceWindow = db.Challenges.Where(x => x.DeviceHash == deviceHash && x.CreatedAt > since);
        if (await deviceWindow.CountAsync(ct) >= 5)
            return RateLimited(await deviceWindow.MinAsync(x => x.CreatedAt, ct), now, ChallengeWindowMinutes);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        var challenge = new LoginChallengeRow
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            RequestKey = requestKey,
            CodeHash = CodeHash(normalized, code),
            ExpiresAt = now.AddMinutes(10),
            CreatedAt = now,
            IpHash = ipHash,
            DeviceHash = deviceHash
        };
        var encrypted = cipher.Encrypt(challenge.Id, normalized, code);
        db.Challenges.Add(challenge);
        db.EmailOutbox.Add(new LoginEmailOutboxRow
        {
            ChallengeId = challenge.Id,
            Ciphertext = encrypted.Ciphertext,
            Nonce = encrypted.Nonce,
            Tag = encrypted.Tag,
            NextAttemptAt = now,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AuthStatus.Accepted);
    }

    private static AuthResult RateLimited(DateTimeOffset oldest, DateTimeOffset now, int windowMinutes)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((oldest.AddMinutes(windowMinutes) - now).TotalSeconds));
        return new(AuthStatus.RateLimited, RetryAfterSeconds: seconds);
    }

    private string RateHash(string purpose, string value) => Convert.ToHexString(
        HMACSHA256.HashData(rateKey, Encoding.UTF8.GetBytes("auth-rate:" + purpose + ":" + value)));

    private static string NormalizeAddress(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8));
        return address.ToString();
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, Guid deviceId,
        IPAddress? remoteAddress, CancellationToken ct)
    {
        var email = Normalize(request.Email);
        if (deviceId == Guid.Empty || email is null || request.Code is null ||
            request.Code.Length != 6 || !request.Code.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 120 ||
            request.Platform is not ("android" or "ios" or "windows" or "macos" or "linux"))
            return new(AuthStatus.Invalid);

        var ipHash = RateHash("login-ip", NormalizeAddress(remoteAddress));
        var deviceHash = RateHash("login-device", deviceId.ToString("N"));
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Match challenge creation's lock order before taking source locks.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({email}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"auth-login-ip:" + ipHash}, 0))", ct);
        var since = now.AddMinutes(-LoginFailureWindowMinutes);
        var ipWindow = db.LoginFailures.Where(x => x.IpHash == ipHash && x.CreatedAt > since);
        if (await ipWindow.CountAsync(ct) >= 40)
            return RateLimited(await ipWindow.MinAsync(x => x.CreatedAt, ct), now, LoginFailureWindowMinutes);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"auth-login-device:" + deviceHash}, 0))", ct);
        var deviceWindow = db.LoginFailures.Where(x => x.DeviceHash == deviceHash && x.CreatedAt > since);
        if (await deviceWindow.CountAsync(ct) >= 10)
            return RateLimited(await deviceWindow.MinAsync(x => x.CreatedAt, ct), now, LoginFailureWindowMinutes);
        var challenge = await db.Challenges.Where(x => x.Email == email)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (challenge is null) return await RecordLoginFailure(ipHash, deviceHash, now, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auth_login_challenges SET attempts=attempts WHERE id={challenge.Id}", ct);
        await db.Entry(challenge).ReloadAsync(ct);
        if (challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= 5)
            return await RecordLoginFailure(ipHash, deviceHash, now, ct);
        var expected = Convert.FromHexString(challenge.CodeHash);
        var provided = Convert.FromHexString(CodeHash(email, request.Code));
        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            challenge.Attempts++;
            return await RecordLoginFailure(ipHash, deviceHash, now, ct);
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email, ct);
        if (user?.DeletedAt is not null)
            return await RecordLoginFailure(ipHash, deviceHash, now, ct);
        if (user is null)
        {
            user = new AuthUserRow { Id = Guid.NewGuid(), Email = email };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO owner_sync_clocks(owner_id) VALUES ({user.Id})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO task_lists(id,owner_id,name,kind) VALUES ({Guid.NewGuid()},{user.Id},'Inbox','inbox')", ct);
        }

        var refresh = NewRefreshToken();
        var session = new DeviceSessionRow
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = request.DeviceName.Trim(),
            Platform = request.Platform,
            RefreshTokenHash = Hash(refresh),
            CreatedAt = now
        };
        challenge.ConsumedAt = now;
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        db.RefreshTokens.Add(new RefreshTokenRow
        {
            TokenHash = session.RefreshTokenHash,
            SessionId = session.Id,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AuthStatus.Success, new(issuer.Issue(user.Id, session.Id), refresh, AccessSeconds));
    }

    private async Task<AuthResult> RecordLoginFailure(string ipHash, string deviceHash,
        DateTimeOffset now, CancellationToken ct)
    {
        db.LoginFailures.Add(new LoginFailureRow
        {
            Id = Guid.NewGuid(),
            IpHash = ipHash,
            DeviceHash = deviceHash,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await db.Database.CurrentTransaction!.CommitAsync(ct);
        return new(AuthStatus.Invalid);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (refreshToken is null || refreshToken.Length != 64 || !refreshToken.All(Uri.IsHexDigit))
            return new(AuthStatus.Invalid);
        var hash = Hash(refreshToken);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token is null) return new(AuthStatus.Invalid);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE refresh_token_history SET consumed_at=consumed_at WHERE token_hash={hash}", ct);
        await db.Entry(token).ReloadAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == token.SessionId, ct);
        if (session is null || session.RevokedAt is not null) return new(AuthStatus.Invalid);
        if (token.ConsumedAt is not null || session.RefreshTokenHash != hash)
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(AuthStatus.Invalid);
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == session.UserId, ct);
        if (user is null || user.DeletedAt is not null) return new(AuthStatus.Invalid);
        var next = NewRefreshToken();
        token.ConsumedAt = now;
        session.RefreshTokenHash = Hash(next);
        db.RefreshTokens.Add(new RefreshTokenRow
        {
            TokenHash = session.RefreshTokenHash,
            SessionId = session.Id,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AuthStatus.Success, new(issuer.Issue(user.Id, session.Id), next, AccessSeconds));
    }

    public Task<bool> SessionActiveAsync(Guid ownerId, Guid sessionId, CancellationToken ct) =>
        db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == ownerId &&
            x.RevokedAt == null && db.Users.Any(u => u.Id == ownerId && u.DeletedAt == null), ct);

    private static string? Normalize(string email)
    {
        if (email is null) return null;
        var value = email.Trim().ToLowerInvariant();
        return value.Length is >= 3 and <= 320 && MailAddress.TryCreate(value, out var address) &&
            address.Address == value ? value : null;
    }

    private string CodeHash(string email, string code) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(codePepper), Encoding.UTF8.GetBytes(email + ":" + code)));

    private static string NewRefreshToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
