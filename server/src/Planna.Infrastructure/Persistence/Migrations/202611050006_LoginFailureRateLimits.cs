using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611050006_LoginFailureRateLimits")]
public sealed class LoginFailureRateLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("auth_login_failures", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false),
            ip_hash = table.Column<string>("text", nullable: false),
            device_hash = table.Column<string>("text", nullable: false),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_auth_login_failures", x => x.id);
            table.CheckConstraint("ck_auth_login_failures_ip_hash", "length(ip_hash) = 64");
            table.CheckConstraint("ck_auth_login_failures_device_hash", "length(device_hash) = 64");
        });
        migrationBuilder.CreateIndex("idx_auth_login_failures_ip_created", "auth_login_failures",
            new[] { "ip_hash", "created_at" }, descending: new[] { false, true });
        migrationBuilder.CreateIndex("idx_auth_login_failures_device_created", "auth_login_failures",
            new[] { "device_hash", "created_at" }, descending: new[] { false, true });
        migrationBuilder.CreateIndex("idx_auth_login_failures_created", "auth_login_failures", "created_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("auth_login_failures");
}
