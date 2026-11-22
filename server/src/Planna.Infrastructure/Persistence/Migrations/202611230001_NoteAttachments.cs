using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611230001_NoteAttachments")]
public sealed class NoteAttachments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("note_attachments", table => new
        {
            id = table.Column<Guid>("uuid", nullable: false),
            owner_id = table.Column<Guid>("uuid", nullable: false),
            note_id = table.Column<Guid>("uuid", nullable: false),
            filename = table.Column<string>("text", nullable: false),
            content_type = table.Column<string>("text", nullable: false),
            size_bytes = table.Column<long>("bigint", nullable: false),
            storage_key = table.Column<string>("text", nullable: false),
            version = table.Column<long>("bigint", nullable: false, defaultValue: 1L),
            created_at = table.Column<DateTime>("timestamp with time zone", nullable: false,
                defaultValueSql: "now()"),
            deleted_at = table.Column<DateTime>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_note_attachments", x => x.id);
            table.ForeignKey("fk_note_attachments_users", x => x.owner_id, "users", "id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("fk_note_attachments_notes", x => x.note_id, "notes", "id",
                onDelete: ReferentialAction.Cascade);
            table.UniqueConstraint("ak_note_attachments_storage_key", x => x.storage_key);
            table.CheckConstraint("ck_note_attachments_filename",
                "length(filename) BETWEEN 1 AND 255");
            table.CheckConstraint("ck_note_attachments_size",
                "size_bytes BETWEEN 1 AND 5242880");
            table.CheckConstraint("ck_note_attachments_content_type",
                "content_type IN ('text/plain','text/markdown','image/png','image/jpeg','application/pdf')");
        });
        migrationBuilder.CreateIndex("idx_note_attachments_owner_note", "note_attachments",
            new[] { "owner_id", "note_id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("note_attachments");
    }
}
