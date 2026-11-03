using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611030002_Spec01Contracts")]
public sealed class Spec01Contracts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("confirmation_token_hash", "ai_plans", "text", nullable: true);
        migrationBuilder.AddColumn<short>("estimate_minutes", "task_steps", "smallint", nullable: true);
        migrationBuilder.AddCheckConstraint("ck_task_steps_estimate_minutes", "task_steps",
            "estimate_minutes BETWEEN 1 AND 1440");

        migrationBuilder.CreateTable("ai_breakdown_drafts", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            task_id = table.Column<Guid>("uuid", nullable: false),
            task_version = table.Column<long>("bigint", nullable: false),
            status = table.Column<string>("text", nullable: false, defaultValue: "awaiting_confirmation"),
            snapshot_hash = table.Column<string>("text", nullable: false),
            steps = table.Column<string>("jsonb", nullable: false),
            assumptions = table.Column<string>("jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
            confirmation_token_hash = table.Column<string>("text", nullable: false),
            expires_at = table.Column<DateTime>("timestamp with time zone", nullable: false),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false, defaultValueSql: "now()"),
            applied_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_ai_breakdown_drafts", x => x.id);
            table.UniqueConstraint("ak_ai_breakdown_drafts_owner_id_id", x => new { x.owner_id, x.id });
            table.ForeignKey("fk_ai_breakdown_drafts_users", x => x.owner_id, "users", "id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("fk_ai_breakdown_drafts_tasks", x => new { x.owner_id, x.task_id }, "tasks", new[] { "owner_id", "id" });
            table.CheckConstraint("ck_ai_breakdown_task_version", "task_version >= 1");
            table.CheckConstraint("ck_ai_breakdown_status", "status IN ('awaiting_confirmation','applied','expired','rejected')");
            table.CheckConstraint("ck_ai_breakdown_steps", "jsonb_typeof(steps) = 'array' AND jsonb_array_length(steps) BETWEEN 1 AND 20");
            table.CheckConstraint("ck_ai_breakdown_assumptions", "jsonb_typeof(assumptions) = 'array'");
        });
        migrationBuilder.CreateIndex("idx_ai_breakdowns_owner_task", "ai_breakdown_drafts", new[] { "owner_id", "task_id", "created_at" }, descending: new[] { false, false, true });

        migrationBuilder.CreateTable("account_exports", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            status = table.Column<string>("text", nullable: false, defaultValue: "pending"),
            snapshot_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            storage_key = table.Column<string>("text", nullable: true),
            error_code = table.Column<string>("text", nullable: true),
            requested_at = table.Column<DateTime>("timestamp with time zone", nullable: false, defaultValueSql: "now()"),
            ready_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            expires_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_account_exports", x => x.id);
            table.UniqueConstraint("ak_account_exports_owner_id_id", x => new { x.owner_id, x.id });
            table.ForeignKey("fk_account_exports_users", x => x.owner_id, "users", "id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_account_exports_status", "status IN ('pending','ready','failed','expired')");
            table.CheckConstraint("ck_account_exports_ready", "(status = 'ready' AND storage_key IS NOT NULL AND ready_at IS NOT NULL AND expires_at IS NOT NULL) OR status <> 'ready'");
        });
        migrationBuilder.CreateIndex("idx_account_exports_owner_time", "account_exports", new[] { "owner_id", "requested_at" }, descending: new[] { false, true });

        migrationBuilder.CreateTable("account_deletion_requests", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            status = table.Column<string>("text", nullable: false, defaultValue: "accepted"),
            policy_version = table.Column<string>("text", nullable: false),
            requested_at = table.Column<DateTime>("timestamp with time zone", nullable: false, defaultValueSql: "now()"),
            completed_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            last_error_code = table.Column<string>("text", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_account_deletion_requests", x => x.id);
            table.UniqueConstraint("ak_account_deletion_requests_owner_id", x => x.owner_id);
            table.ForeignKey("fk_account_deletion_requests_users", x => x.owner_id, "users", "id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_account_deletion_requests_status", "status IN ('accepted','purging','completed','failed')");
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("account_deletion_requests");
        migrationBuilder.DropTable("account_exports");
        migrationBuilder.DropTable("ai_breakdown_drafts");
        migrationBuilder.DropColumn("confirmation_token_hash", "ai_plans");
        migrationBuilder.DropColumn("estimate_minutes", "task_steps");
    }
}
