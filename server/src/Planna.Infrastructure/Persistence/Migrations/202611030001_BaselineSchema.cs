using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Planna.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlannaSchemaContext))]
[Migration("202611030001_BaselineSchema")]
public sealed class BaselineSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(BaselineSchema).Assembly.GetManifestResourceStream("Planna.BaselineSql")
            ?? throw new InvalidOperationException("Embedded baseline SQL is missing.");
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Baseline rollback is destructive; restore an isolated database instead.");
    }
}
