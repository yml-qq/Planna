using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611140001_FocusSessions")]
public sealed class FocusSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("focus_sessions", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            task_id = table.Column<Guid>("uuid", nullable: true),
            started_at = table.Column<DateTime>("timestamp with time zone", nullable: false),
            ended_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            planned_seconds = table.Column<int>("integer", nullable: false, defaultValue: 1500),
            actual_seconds = table.Column<int>("integer", nullable: false, defaultValue: 0),
            break_seconds = table.Column<int>("integer", nullable: false, defaultValue: 0),
            state = table.Column<string>("text", nullable: false, defaultValue: "active"),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_focus_sessions", x => x.id);
            table.ForeignKey("fk_focus_sessions_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("fk_focus_sessions_tasks", x => new { x.owner_id, x.task_id },
                "tasks", new[] { "owner_id", "id" }, onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_focus_sessions_planned", "planned_seconds BETWEEN 60 AND 86400");
            table.CheckConstraint("ck_focus_sessions_actual", "actual_seconds BETWEEN 0 AND 86400");
            table.CheckConstraint("ck_focus_sessions_break", "break_seconds BETWEEN 0 AND 86400");
            table.CheckConstraint("ck_focus_sessions_state",
                "state IN ('active','paused','completed','abandoned')");
            table.CheckConstraint("ck_focus_sessions_ended",
                "ended_at IS NULL OR ended_at >= started_at");
        });
        migrationBuilder.CreateIndex("idx_focus_sessions_owner_started", "focus_sessions",
            new[] { "owner_id", "started_at", "id" });
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_focus_sessions_open ON focus_sessions(owner_id)
            WHERE state IN ('active','paused');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("focus_sessions");
    }
}
