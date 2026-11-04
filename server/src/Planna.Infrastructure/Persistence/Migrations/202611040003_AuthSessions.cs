using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611040003_AuthSessions")]
public sealed class AuthSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("auth_login_challenges", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
            email = table.Column<string>("text", nullable: false),
            request_key = table.Column<Guid>("uuid", nullable: false),
            code_hash = table.Column<string>("text", nullable: false),
            expires_at = table.Column<DateTime>("timestamp with time zone", nullable: false),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false, defaultValueSql: "now()"),
            consumed_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            attempts = table.Column<int>("integer", nullable: false, defaultValue: 0)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_auth_login_challenges", x => x.id);
            table.CheckConstraint("ck_auth_login_challenges_attempts", "attempts BETWEEN 0 AND 5");
        });
        migrationBuilder.CreateIndex("idx_auth_challenges_email_created", "auth_login_challenges",
            new[] { "email", "created_at" }, descending: new[] { false, true });
        migrationBuilder.CreateIndex("uq_auth_challenges_request", "auth_login_challenges",
            new[] { "email", "request_key" }, unique: true);

        migrationBuilder.CreateTable("refresh_token_history", table => new
        {
            token_hash = table.Column<string>("text", nullable: false),
            session_id = table.Column<Guid>("uuid", nullable: false),
            consumed_at = table.Column<DateTime>("timestamp with time zone", nullable: true),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false, defaultValueSql: "now()")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_refresh_token_history", x => x.token_hash);
            table.ForeignKey("fk_refresh_token_history_session", x => x.session_id,
                "device_sessions", "id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("idx_refresh_token_history_session", "refresh_token_history", "session_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("refresh_token_history");
        migrationBuilder.DropTable("auth_login_challenges");
    }
}
