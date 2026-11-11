using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611130001_Habits")]
public sealed class Habits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("habits", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            title = table.Column<string>("text", nullable: false),
            schedule_type = table.Column<string>("text", nullable: false),
            weekdays = table.Column<short[]>("smallint[]", nullable: true),
            target_quantity = table.Column<int>("integer", nullable: false, defaultValue: 1),
            unit = table.Column<string>("text", nullable: false, defaultValue: "count"),
            time_zone = table.Column<string>("text", nullable: false),
            paused_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            deleted_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_habits", x => x.id);
            table.UniqueConstraint("ak_habits_owner_id_id", x => new { x.owner_id, x.id });
            table.ForeignKey("fk_habits_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_habits_title", "char_length(title) BETWEEN 1 AND 200");
            table.CheckConstraint("ck_habits_schedule",
                "schedule_type IN ('daily','weekly','weekdays')");
            table.CheckConstraint("ck_habits_target", "target_quantity BETWEEN 1 AND 1000");
            table.CheckConstraint("ck_habits_unit", "char_length(unit) BETWEEN 1 AND 20");
            table.CheckConstraint("ck_habits_weekdays",
                "(schedule_type = 'weekly' AND weekdays IS NOT NULL AND array_length(weekdays,1) BETWEEN 1 AND 7) OR " +
                "(schedule_type <> 'weekly' AND weekdays IS NULL)");
        });
        migrationBuilder.CreateIndex("idx_habits_owner_created", "habits",
            new[] { "owner_id", "created_at" }, descending: new[] { false, true });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("habits");
    }
}
