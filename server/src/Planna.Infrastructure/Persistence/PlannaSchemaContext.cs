using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Planna.Infrastructure.Persistence;

// Migration-only context while the application command model is built in later slices.
public sealed class PlannaSchemaContext(DbContextOptions<PlannaSchemaContext> options) : DbContext(options);

public sealed class PlannaSchemaContextFactory : IDesignTimeDbContextFactory<PlannaSchemaContext>
{
    public PlannaSchemaContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLANNADB_CONNECTION")
            ?? "Host=localhost;Database=planna;Username=planna;Password=local-dev-only";
        var options = new DbContextOptionsBuilder<PlannaSchemaContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(PlannaSchemaContext).Assembly.FullName))
            .Options;
        return new PlannaSchemaContext(options);
    }
}
