using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Planna.Application.Identity;
using Planna.Infrastructure.Persistence;

namespace Planna.Infrastructure.Identity;

// Step-up verification for sensitive actions: a short email code proves mailbox control and
// yields a signed, purpose-bound, owner-bound token. Codes are stored only as HMAC hashes.
public sealed class StepUpService(AuthContext db, LoginCodeCipher cipher, TimeProvider clock, string codePepper)
    : IStepUpService
{
    private const int ChallengeWindowMinutes = 15;
    private const int MaxChallengesPerWindow = 3;
    private const int MaxAttempts = 5;
    private const int ChallengeTtlMinutes = 10;
    private const int TokenTtlMinutes = 10;
    private const string Purpose = "account_delete";
    private readonly byte[] key = Encoding.UTF8.GetBytes(codePepper);

    public async Task<StepUpResult> ChallengeAsync(Guid ownerId, Guid requestKey, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || requestKey == Guid.Empty) return new(StepUpStatus.Invalid);
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0))",
            new { Key = "step-up:" + ownerId.ToString("N") }, transaction.GetDbTransaction(), cancellationToken: ct));
        var email = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT email FROM users WHERE id=@Owner AND deleted_at IS NULL",
            new { Owner = ownerId }, transaction.GetDbTransaction(), cancellationToken: ct));
        if (email is null) return new(StepUpStatus.AccountDeleted);
        var replay = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT expires_at FROM auth_step_up_challenges WHERE user_id=@Owner AND request_key=@Key",
            new { Owner = ownerId, Key = requestKey }, transaction.GetDbTransaction(), cancellationToken: ct));
        if (replay is { } replayExpires)
        {
            await transaction.RollbackAsync(ct);
            return new(StepUpStatus.Accepted, ExpiresAt: Instant(replayExpires));
        }
        var since = now.AddMinutes(-ChallengeWindowMinutes);
        var window = await connection.QuerySingleAsync<WindowRow>(new CommandDefinition("""
            SELECT count(*) AS "Count", min(created_at) AS "Oldest"
            FROM auth_step_up_challenges WHERE user_id=@Owner AND created_at > @Since
            """, new { Owner = ownerId, Since = since.UtcDateTime }, transaction.GetDbTransaction(),
            cancellationToken: ct));
        if (window.Count >= MaxChallengesPerWindow)
        {
            var retry = window.Oldest is { } oldest
                ? Math.Max(1, (int)Math.Ceiling((oldest.AddMinutes(ChallengeWindowMinutes) - now).TotalSeconds))
                : ChallengeWindowMinutes * 60;
            await transaction.RollbackAsync(ct);
            return new(StepUpStatus.RateLimited, RetryAfterSeconds: retry);
        }
        var challengeId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        // PostgreSQL timestamptz stores microseconds; truncate so replay returns the identical instant.
        var expiresAt = TruncateToMicroseconds(now.AddMinutes(ChallengeTtlMinutes));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO auth_step_up_challenges(id, user_id, purpose, request_key, code_hash, created_at, expires_at)
            VALUES(@Id, @Owner, 'account_delete', @Key, @Hash, @Now, @Expires)
            """, new
        {
            Id = challengeId,
            Owner = ownerId,
            Key = requestKey,
            Hash = CodeHash(ownerId, code),
            Now = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime
        }, transaction.GetDbTransaction(), cancellationToken: ct));
        var encrypted = cipher.Encrypt(challengeId, email, code);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO auth_step_up_email_outbox(challenge_id, ciphertext, nonce, tag, next_attempt_at, created_at)
            VALUES(@Challenge, @Ciphertext, @Nonce, @Tag, @Now, @Now)
            """, new
        {
            Challenge = challengeId,
            Ciphertext = encrypted.Ciphertext,
            Nonce = encrypted.Nonce,
            Tag = encrypted.Tag,
            Now = now.UtcDateTime
        }, transaction.GetDbTransaction(), cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new(StepUpStatus.Accepted, expiresAt);
    }

    public async Task<StepUpResult> VerifyAsync(Guid ownerId, string? purpose, string? code, CancellationToken ct)
    {
        if (ownerId == Guid.Empty || purpose != Purpose || code is null ||
            code.Length != 6 || !code.All(char.IsAsciiDigit))
            return new(StepUpStatus.Invalid);
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0))",
            new { Key = "step-up:" + ownerId.ToString("N") }, transaction.GetDbTransaction(), cancellationToken: ct));
        var challenge = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(new CommandDefinition("""
            SELECT id AS "Id", code_hash AS "CodeHash", attempts AS "Attempts", expires_at AS "ExpiresAt"
            FROM auth_step_up_challenges
            WHERE user_id=@Owner AND purpose='account_delete' AND consumed_at IS NULL
            ORDER BY created_at DESC LIMIT 1
            """, new { Owner = ownerId }, transaction.GetDbTransaction(), cancellationToken: ct));
        if (challenge is null) return new(StepUpStatus.Invalid);
        if (challenge.ExpiresAt <= now.UtcDateTime) return new(StepUpStatus.Invalid);
        if (challenge.Attempts >= MaxAttempts)
            return new(StepUpStatus.RateLimited, RetryAfterSeconds: 60);
        var expected = CodeHash(ownerId, code);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(challenge.CodeHash)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE auth_step_up_challenges SET attempts=attempts+1 WHERE id=@Id",
                new { challenge.Id }, transaction.GetDbTransaction(), cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return new(StepUpStatus.Invalid);
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE auth_step_up_challenges SET consumed_at=@Now WHERE id=@Id",
            new { Now = now.UtcDateTime, challenge.Id }, transaction.GetDbTransaction(), cancellationToken: ct));
        var tokenExpires = now.AddMinutes(TokenTtlMinutes);
        var token = SignToken(ownerId, Purpose, tokenExpires);
        await transaction.CommitAsync(ct);
        return new(StepUpStatus.Success, tokenExpires, token);
    }

    public bool TryValidateToken(Guid ownerId, string? purpose, string? token)
    {
        if (ownerId == Guid.Empty || purpose != Purpose || string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        var expected = Sign(parts[0]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(parts[1])))
            return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        }
        catch (FormatException)
        {
            return false;
        }
        var segments = decoded.Split('|');
        if (segments.Length != 3 || segments[0] != ownerId.ToString("N") || segments[1] != Purpose)
            return false;
        return long.TryParse(segments[2], out var unix) && unix > clock.GetUtcNow().ToUnixTimeSeconds();
    }

    private string SignToken(Guid ownerId, string purpose, DateTimeOffset expires)
    {
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
            ownerId.ToString("N") + "|" + purpose + "|" + expires.ToUnixTimeSeconds()));
        return payload + "." + Sign(payload);
    }

    private string Sign(string payload) => Base64UrlEncode(
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("step-up-token:" + payload)));

    private string CodeHash(Guid ownerId, string code) => Convert.ToHexString(HMACSHA256.HashData(key,
        Encoding.UTF8.GetBytes("step-up-code:" + ownerId.ToString("N") + ":" + code))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private sealed class WindowRow
    {
        public long Count { get; set; }
        public DateTime? Oldest { get; set; }
    }

    private sealed class ChallengeRow
    {
        public Guid Id { get; set; }
        public string CodeHash { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
