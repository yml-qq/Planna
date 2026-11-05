using Microsoft.EntityFrameworkCore;

namespace Planna.Infrastructure.Persistence;

public sealed class AuthContext(DbContextOptions<AuthContext> options) : DbContext(options)
{
    public DbSet<AuthUserRow> Users => Set<AuthUserRow>();
    public DbSet<LoginChallengeRow> Challenges => Set<LoginChallengeRow>();
    public DbSet<DeviceSessionRow> Sessions => Set<DeviceSessionRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();
    public DbSet<LoginEmailOutboxRow> EmailOutbox => Set<LoginEmailOutboxRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AuthUserRow>(e =>
        {
            e.ToTable("users", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });
        model.Entity<LoginChallengeRow>(e =>
        {
            e.ToTable("auth_login_challenges", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.RequestKey).HasColumnName("request_key");
            e.Property(x => x.CodeHash).HasColumnName("code_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.IpHash).HasColumnName("ip_hash");
            e.Property(x => x.DeviceHash).HasColumnName("device_hash");
        });
        model.Entity<DeviceSessionRow>(e =>
        {
            e.ToTable("device_sessions", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DeviceName).HasColumnName("device_name");
            e.Property(x => x.Platform).HasColumnName("platform");
            e.Property(x => x.RefreshTokenHash).HasColumnName("refresh_token_hash");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<RefreshTokenRow>(e =>
        {
            e.ToTable("refresh_token_history", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.TokenHash);
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<LoginEmailOutboxRow>(e =>
        {
            e.ToTable("auth_email_outbox", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.ChallengeId);
            e.HasOne<LoginChallengeRow>().WithOne()
                .HasForeignKey<LoginEmailOutboxRow>(x => x.ChallengeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ChallengeId).HasColumnName("challenge_id");
            e.Property(x => x.Ciphertext).HasColumnName("ciphertext");
            e.Property(x => x.Nonce).HasColumnName("nonce");
            e.Property(x => x.Tag).HasColumnName("tag");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.LeaseToken).HasColumnName("lease_token");
            e.Property(x => x.LeaseUntil).HasColumnName("lease_until");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}

public sealed class AuthUserRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class LoginChallengeRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public Guid RequestKey { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int Attempts { get; set; }
    public string? IpHash { get; set; }
    public string? DeviceHash { get; set; }
}

public sealed class DeviceSessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string RefreshTokenHash { get; set; } = "";
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RefreshTokenRow
{
    public string TokenHash { get; set; } = "";
    public Guid SessionId { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LoginEmailOutboxRow
{
    public Guid ChallengeId { get; set; }
    public byte[]? Ciphertext { get; set; }
    public byte[]? Nonce { get; set; }
    public byte[]? Tag { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
