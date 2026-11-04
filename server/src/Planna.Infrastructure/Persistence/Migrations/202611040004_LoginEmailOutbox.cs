using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611040004_LoginEmailOutbox")]
public sealed class LoginEmailOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("auth_email_outbox", table => new
        {
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
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_auth_email_outbox", x => x.challenge_id);
            table.ForeignKey("fk_auth_email_outbox_challenge", x => x.challenge_id,
                "auth_login_challenges", "id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_auth_email_outbox_status",
                "status IN ('pending','leased','sent','expired','failed')");
            table.CheckConstraint("ck_auth_email_outbox_attempts", "attempts BETWEEN 0 AND 5");
            table.CheckConstraint("ck_auth_email_outbox_payload",
                "(status IN ('pending','leased') AND ciphertext IS NOT NULL AND nonce IS NOT NULL AND tag IS NOT NULL) OR " +
                "(status IN ('sent','expired','failed') AND ciphertext IS NULL AND nonce IS NULL AND tag IS NULL)");
        });
        migrationBuilder.CreateIndex("idx_auth_email_outbox_due", "auth_email_outbox",
            new[] { "next_attempt_at", "lease_until" },
            filter: "status IN ('pending','leased')");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("auth_email_outbox");
}
