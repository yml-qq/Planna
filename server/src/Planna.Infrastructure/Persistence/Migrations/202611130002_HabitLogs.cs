using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611130002_HabitLogs")]
public sealed class HabitLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("habit_logs", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            habit_id = table.Column<Guid>("uuid", nullable: false),
            log_date = table.Column<DateOnly>("date", nullable: false),
            quantity = table.Column<int>("integer", nullable: false, defaultValue: 1),
            status = table.Column<string>("text", nullable: false, defaultValue: "done"),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_habit_logs", x => x.id);
            table.UniqueConstraint("ak_habit_logs_day", x => new { x.owner_id, x.habit_id, x.log_date });
            table.ForeignKey("fk_habit_logs_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("fk_habit_logs_habits", x => new { x.owner_id, x.habit_id }, "habits",
                new[] { "owner_id", "id" }, onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_habit_logs_quantity", "quantity BETWEEN 0 AND 1000");
            table.CheckConstraint("ck_habit_logs_status", "status IN ('done','skipped')");
        });
        migrationBuilder.CreateIndex("idx_habit_logs_owner_habit_date", "habit_logs",
            new[] { "owner_id", "habit_id", "log_date" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("habit_logs");
    }
}
