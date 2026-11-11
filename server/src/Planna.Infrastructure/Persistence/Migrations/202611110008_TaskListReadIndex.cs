using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611110008_TaskListReadIndex")]
public sealed class TaskListReadIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("idx_tasks_owner_created", "tasks",
            new[] { "owner_id", "created_at", "id" },
            descending: new[] { false, true, true }, filter: "deleted_at IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("idx_tasks_owner_created", "tasks");
    }
}
