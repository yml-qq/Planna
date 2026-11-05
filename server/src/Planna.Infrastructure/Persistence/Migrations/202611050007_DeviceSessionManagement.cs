using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611050007_DeviceSessionManagement")]
public sealed class DeviceSessionManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("version", "device_sessions", "integer",
            nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<Guid>("revocation_key", "device_sessions", "uuid", nullable: true);
        migrationBuilder.AddCheckConstraint("ck_device_sessions_version", "device_sessions", "version >= 1");
        migrationBuilder.CreateIndex("idx_device_sessions_active_page", "device_sessions",
            new[] { "user_id", "created_at", "id" },
            descending: new[] { false, true, true }, filter: "revoked_at IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("idx_device_sessions_active_page", "device_sessions");
        migrationBuilder.DropCheckConstraint("ck_device_sessions_version", "device_sessions");
        migrationBuilder.DropColumn("revocation_key", "device_sessions");
        migrationBuilder.DropColumn("version", "device_sessions");
    }
}
