using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611230002_AttachmentRateWindows")]
public sealed class AttachmentRateWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("attachment_rate_windows", table => new
        {
            owner_id = table.Column<Guid>("uuid", nullable: false),
            window_start = table.Column<DateTime>("timestamp with time zone", nullable: false),
            count = table.Column<int>("integer", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_attachment_rate_windows", x => x.owner_id);
            table.ForeignKey("fk_attachment_rate_windows_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_attachment_rate_windows_count", "count >= 0");
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("attachment_rate_windows");
    }
}
