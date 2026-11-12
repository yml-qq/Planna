using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611140002_Goals")]
public sealed class Goals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("goals", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            title = table.Column<string>("text", nullable: false),
            description = table.Column<string>("text", nullable: false, defaultValue: ""),
            target_date = table.Column<DateOnly>("date", nullable: true),
            status = table.Column<string>("text", nullable: false, defaultValue: "active"),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            deleted_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_goals", x => x.id);
            table.ForeignKey("fk_goals_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_goals_title", "length(btrim(title)) BETWEEN 1 AND 200");
            table.CheckConstraint("ck_goals_description", "length(description) <= 2000");
            table.CheckConstraint("ck_goals_status", "status IN ('active','completed','archived')");
        });
        migrationBuilder.CreateIndex("idx_goals_owner_status", "goals", new[] { "owner_id", "status" });

        migrationBuilder.CreateTable("milestones", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            goal_id = table.Column<Guid>("uuid", nullable: false),
            title = table.Column<string>("text", nullable: false),
            due_date = table.Column<DateOnly>("date", nullable: true),
            completed = table.Column<bool>("boolean", nullable: false, defaultValue: false),
            position = table.Column<int>("integer", nullable: false, defaultValue: 0),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            deleted_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_milestones", x => x.id);
            table.ForeignKey("fk_milestones_goals", x => new { x.owner_id, x.goal_id }, "goals",
                new[] { "owner_id", "id" }, onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_milestones_title", "length(btrim(title)) BETWEEN 1 AND 200");
        });
        migrationBuilder.CreateIndex("idx_milestones_owner_goal", "milestones",
            new[] { "owner_id", "goal_id", "position" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("milestones");
        migrationBuilder.DropTable("goals");
    }
}
