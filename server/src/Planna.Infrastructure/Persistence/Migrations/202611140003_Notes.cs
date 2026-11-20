using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611140003_Notes")]
public sealed class Notes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("notes", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            title = table.Column<string>("text", nullable: false, defaultValue: ""),
            content = table.Column<string>("text", nullable: false, defaultValue: ""),
            task_id = table.Column<Guid>("uuid", nullable: true),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            updated_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            deleted_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_notes", x => x.id);
            table.ForeignKey("fk_notes_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("fk_notes_tasks", x => x.task_id, "tasks", "id",
                onDelete: ReferentialAction.SetNull);
            table.CheckConstraint("ck_notes_title", "length(title) <= 200");
            table.CheckConstraint("ck_notes_content", "length(content) <= 20000");
        });
        migrationBuilder.CreateIndex("idx_notes_owner_updated", "notes",
            new[] { "owner_id", "updated_at" });
        migrationBuilder.CreateIndex("idx_notes_owner_task", "notes", new[] { "owner_id", "task_id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("notes");
    }
}
