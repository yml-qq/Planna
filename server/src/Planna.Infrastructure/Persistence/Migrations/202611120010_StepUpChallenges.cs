using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611120010_StepUpChallenges")]
public sealed class StepUpChallenges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("auth_step_up_challenges", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            user_id = table.Column<Guid>("uuid", nullable: false),
            purpose = table.Column<string>("text", nullable: false, defaultValue: "account_delete"),
            request_key = table.Column<Guid>("uuid", nullable: false),
            code_hash = table.Column<string>("text", nullable: false),
            attempts = table.Column<int>("integer", nullable: false, defaultValue: 0),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            expires_at = table.Column<DateTime>("timestamp with time zone", nullable: false),
            consumed_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_auth_step_up_challenges", x => x.id);
            table.UniqueConstraint("ak_auth_step_up_request", x => new { x.user_id, x.request_key });
            table.ForeignKey("fk_auth_step_up_users", x => x.user_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_auth_step_up_purpose", "purpose IN ('account_delete')");
            table.CheckConstraint("ck_auth_step_up_attempts", "attempts BETWEEN 0 AND 5");
        });
        migrationBuilder.CreateIndex("idx_auth_step_up_user_created", "auth_step_up_challenges",
            new[] { "user_id", "created_at" }, descending: new[] { false, true });

        migrationBuilder.CreateTable("auth_step_up_email_outbox", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            challenge_id = table.Column<Guid>("uuid", nullable: false),
            ciphertext = table.Column<byte[]>("bytea", nullable: true),
            nonce = table.Column<byte[]>("bytea", nullable: true),
            tag = table.Column<byte[]>("bytea", nullable: true),
            status = table.Column<string>("text", nullable: false, defaultValue: "pending"),
            attempts = table.Column<int>("integer", nullable: false, defaultValue: 0),
            next_attempt_at = table.Column<DateTime>("timestamp with time zone", nullable: false),
            lease_token = table.Column<Guid>("uuid", nullable: true),
            lease_until = table.Column<DateTime>("timestamp with time zone", nullable: true),
            sent_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_auth_step_up_outbox", x => x.id);
            table.UniqueConstraint("ak_auth_step_up_outbox_challenge", x => x.challenge_id);
            table.ForeignKey("fk_auth_step_up_outbox_challenge", x => x.challenge_id,
                "auth_step_up_challenges", "id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_auth_step_up_outbox_status",
                "status IN ('pending','leased','sent','failed','expired')");
            table.CheckConstraint("ck_auth_step_up_outbox_attempts", "attempts BETWEEN 0 AND 5");
        });
        migrationBuilder.CreateIndex("idx_auth_step_up_outbox_due", "auth_step_up_email_outbox",
            new[] { "status", "next_attempt_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("auth_step_up_email_outbox");
        migrationBuilder.DropTable("auth_step_up_challenges");
    }
}
