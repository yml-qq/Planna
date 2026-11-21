using Microsoft.EntityFrameworkCore;
using Planna.Infrastructure.Persistence;

// Production migration entry point. Credentials come only from the environment/secret manager;
// this tool never reads or prints connection strings and exits non-zero on failure.
var connection = Environment.GetEnvironmentVariable("PLANNADB_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("PLANNADB_CONNECTION is required.");
    return 2;
}

var options = new DbContextOptionsBuilder<PlannaSchemaContext>()
    .UseNpgsql(connection, npgsql =>
        npgsql.MigrationsAssembly(typeof(PlannaSchemaContext).Assembly.FullName))
    .Options;
await using var context = new PlannaSchemaContext(options);
var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
Console.WriteLine($"pending migrations: {pending.Count}");
foreach (var migration in pending) Console.WriteLine($"  {migration}");
await context.Database.MigrateAsync();
Console.WriteLine("database is up to date");
return 0;
