using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611050005_AuthChallengeRateLimits")]
public sealed class AuthChallengeRateLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ip_hash", "auth_login_challenges", "text", nullable: true);
        migrationBuilder.AddColumn<string>("device_hash", "auth_login_challenges", "text", nullable: true);
        migrationBuilder.CreateIndex("idx_auth_challenges_ip_created", "auth_login_challenges",
            new[] { "ip_hash", "created_at" }, descending: new[] { false, true },
            filter: "ip_hash IS NOT NULL");
        migrationBuilder.CreateIndex("idx_auth_challenges_device_created", "auth_login_challenges",
            new[] { "device_hash", "created_at" }, descending: new[] { false, true },
            filter: "device_hash IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("idx_auth_challenges_device_created", "auth_login_challenges");
        migrationBuilder.DropIndex("idx_auth_challenges_ip_created", "auth_login_challenges");
        migrationBuilder.DropColumn("device_hash", "auth_login_challenges");
        migrationBuilder.DropColumn("ip_hash", "auth_login_challenges");
    }
}
