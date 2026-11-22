using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611220001_WorkdayOverrides")]
public sealed class WorkdayOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("workday_overrides", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            jurisdiction = table.Column<string>("text", nullable: false, defaultValue: "CN"),
            date = table.Column<DateOnly>("date", nullable: false),
            kind = table.Column<string>("text", nullable: false),
            note = table.Column<string>("text", nullable: false, defaultValue: ""),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_workday_overrides", x => x.id);
            table.ForeignKey("fk_workday_overrides_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.UniqueConstraint("ak_workday_overrides_owner_jurisdiction_date",
                x => new { x.owner_id, x.jurisdiction, x.date });
            table.CheckConstraint("ck_workday_overrides_kind", "kind IN ('holiday','workday')");
            table.CheckConstraint("ck_workday_overrides_jurisdiction",
                "length(jurisdiction) BETWEEN 1 AND 32");
            table.CheckConstraint("ck_workday_overrides_note", "length(note) <= 200");
        });
        migrationBuilder.CreateIndex("idx_workday_overrides_owner_date", "workday_overrides",
            new[] { "owner_id", "date" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("workday_overrides");
    }
}
