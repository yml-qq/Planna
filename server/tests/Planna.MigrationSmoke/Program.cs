using Microsoft.EntityFrameworkCore;
using Npgsql;
using Planna.Application.Tasks;
using Planna.Infrastructure.Persistence;
using Planna.Infrastructure.Tasks;

var value = Environment.GetEnvironmentVariable("PLANNADB_CONNECTION")
    ?? throw new InvalidOperationException("Set PLANNADB_CONNECTION to an isolated local test database.");
var target = new NpgsqlConnectionStringBuilder(value);
if (target.Host is not ("localhost" or "127.0.0.1") ||
    target.Database is not { } database ||
    !database.StartsWith("planna_test", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Migration smoke only runs against localhost/planna_test*.");

await using (var db = new PlannaSchemaContextFactory().CreateDbContext([]))
{
    await db.Database.MigrateAsync();
    await db.Database.MigrateAsync();
}

await using var connection = new NpgsqlConnection(value);
await connection.OpenAsync();

static async Task<int> Exec(NpgsqlConnection db, string sql, params object[] values)
{
    await using var cmd = new NpgsqlCommand(sql, db);
    foreach (var item in values) cmd.Parameters.AddWithValue(item);
    return await cmd.ExecuteNonQueryAsync();
}

static async Task Expect(string state, Func<Task> action)
{
    try { await action(); }
    catch (PostgresException ex) when (ex.SqlState == state) { return; }
    throw new InvalidOperationException($"Expected SQLSTATE {state}.");
}

var owner = Guid.NewGuid();
var other = Guid.NewGuid();
var list = Guid.NewGuid();
var task = Guid.NewGuid();
await Exec(connection, "INSERT INTO users(id,email) VALUES($1,$2),($3,$4)", owner, "one@example.test", other, "two@example.test");
await Exec(connection, "INSERT INTO task_lists(id,owner_id,name) VALUES($1,$2,'Inbox')", list, owner);
await Exec(connection, "INSERT INTO tasks(id,owner_id,list_id,title) VALUES($1,$2,$3,'Synthetic task')", task, owner, list);

const string draft = "INSERT INTO ai_breakdown_drafts(owner_id,task_id,task_version,snapshot_hash,steps,confirmation_token_hash,expires_at) VALUES($1,$2,1,'hash','[{\"step_id\":\"synthetic\"}]','token',now()+interval '1 hour')";
await Expect("23503", () => Exec(connection, draft, other, task));
await Expect("23514", () => Exec(connection, draft.Replace("'[{\"step_id\":\"synthetic\"}]'", "'[]'"), owner, task));
await Exec(connection, draft, owner, task);
await Expect("23514", () => Exec(connection,
    "INSERT INTO task_steps(id,owner_id,task_id,title,estimate_minutes) VALUES($1,$2,$3,'Invalid',0)", Guid.NewGuid(), owner, task));
await Expect("23514", () => Exec(connection, "INSERT INTO account_exports(owner_id,status) VALUES($1,'ready')", owner));
await Exec(connection, "INSERT INTO account_deletion_requests(owner_id,policy_version) VALUES($1,'test-v1')", owner);
await Expect("23505", () => Exec(connection,
    "INSERT INTO account_deletion_requests(owner_id,policy_version) VALUES($1,'test-v1')", owner));

var mutation = Guid.NewGuid();
await using (var tx = await connection.BeginTransactionAsync())
{
    await Exec(connection, "INSERT INTO client_mutations(owner_id,mutation_id,request_hash,response) VALUES($1,$2,'hash','{}')", owner, mutation);
    await Exec(connection, "INSERT INTO task_steps(id,owner_id,task_id,title,estimate_minutes) VALUES($1,$2,$3,'Rollback',15)", Guid.NewGuid(), owner, task);
    await tx.RollbackAsync();
}
await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM client_mutations WHERE owner_id=$1 AND mutation_id=$2", connection))
{
    cmd.Parameters.AddWithValue(owner);
    cmd.Parameters.AddWithValue(mutation);
    if ((long)(await cmd.ExecuteScalarAsync() ?? -1L) != 0) throw new InvalidOperationException("Rollback failed.");
}
await Exec(connection, "INSERT INTO client_mutations(owner_id,mutation_id,request_hash,response) VALUES($1,$2,'hash','{}')", owner, mutation);
await Expect("23505", () => Exec(connection,
    "INSERT INTO client_mutations(owner_id,mutation_id,request_hash,response) VALUES($1,$2,'hash','{}')", owner, mutation));
Console.WriteLine("PASS: migrations/reapply, owner FK, constraints, rollback, duplicate mutation/deletion");

await Exec(connection, "INSERT INTO owner_sync_clocks(owner_id) VALUES($1),($2)", owner, other);
var options = new DbContextOptionsBuilder<TaskWriteContext>().UseNpgsql(value).Options;
var createRequest = new CreateTaskRequest(Guid.NewGuid(), list, "  Test task  ", null, 2,
    null, null, null, false, "Asia/Shanghai", 25);
var createKey = Guid.NewGuid();
async Task<CreateTaskResult> Create(Guid ownerId, Guid key, CreateTaskRequest body)
{
    await using var writeDb = new TaskWriteContext(options);
    return await new CreateTaskService(writeDb, TimeProvider.System)
        .ExecuteAsync(ownerId, key, body, CancellationToken.None);
}

var first = await Create(owner, createKey, createRequest);
if (first.Status != CreateTaskStatus.Created || first.Task?.Title != "Test task")
    throw new InvalidOperationException($"Create failed: {first.Status}/{first.Code}");
var replay = await Create(owner, createKey, createRequest);
if (replay.Status != CreateTaskStatus.Replayed || replay.Task != first.Task)
    throw new InvalidOperationException("Idempotent replay failed.");
var changed = await Create(owner, createKey, createRequest with { Title = "Changed" });
if (changed.Status != CreateTaskStatus.Conflict)
    throw new InvalidOperationException("Reused idempotency key was accepted.");
var foreign = await Create(other, Guid.NewGuid(), createRequest with { Id = Guid.NewGuid() });
if (foreign.Status != CreateTaskStatus.ListNotFound)
    throw new InvalidOperationException("Foreign list was accepted.");

var concurrent = await Task.WhenAll(
    Create(owner, Guid.NewGuid(), createRequest with { Id = Guid.NewGuid(), Title = "Concurrent A" }),
    Create(owner, Guid.NewGuid(), createRequest with { Id = Guid.NewGuid(), Title = "Concurrent B" }));
if (concurrent.Any(x => x.Status != CreateTaskStatus.Created))
    throw new InvalidOperationException("Concurrent creates did not both commit.");
var collision = await Create(owner, Guid.NewGuid(), createRequest with { Title = "Collision" });
if (collision.Status != CreateTaskStatus.Conflict)
    throw new InvalidOperationException("Duplicate task ID was accepted.");

await Exec(connection, "UPDATE users SET deleted_at=now() WHERE id=$1", owner);
var deleted = await Create(owner, Guid.NewGuid(), createRequest with { Id = Guid.NewGuid() });
if (deleted.Status != CreateTaskStatus.AccountDeleted)
    throw new InvalidOperationException("Deleted owner could create a task.");

async Task<long> Scalar(string sql, params object[] values)
{
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var item in values) command.Parameters.AddWithValue(item);
    return (long)(await command.ExecuteScalarAsync() ?? -1L);
}
if (await Scalar("SELECT revision FROM owner_sync_clocks WHERE owner_id=$1", owner) != 3 ||
    await Scalar("SELECT count(*) FROM sync_changes WHERE owner_id=$1", owner) != 3 ||
    await Scalar("SELECT count(DISTINCT revision) FROM sync_changes WHERE owner_id=$1", owner) != 3 ||
    await Scalar("SELECT count(*) FROM client_mutations WHERE owner_id=$1 AND mutation_id=$2", owner, createKey) != 1)
    throw new InvalidOperationException("Create, sync change and mutation result were not atomic.");
Console.WriteLine("PASS: create task, replay, owner isolation, deleted account, concurrent revisions, conflict rollback");
await HttpCreateTaskSmoke.RunAsync(connection);
await HttpAuthSmoke.RunAsync(connection);
await AuthMaintenanceSmoke.RunAsync(connection);
await HttpDeviceSmoke.RunAsync(connection);
await HttpTaskReadSmoke.RunAsync(connection);
await HttpTaskWriteSmoke.RunAsync(connection);
await HttpTaskCompleteSmoke.RunAsync(connection);
await HttpListSmoke.RunAsync(connection);
await HttpTagSmoke.RunAsync(connection);
await HttpTaskTagSmoke.RunAsync(connection);
await HttpStepSmoke.RunAsync(connection);
await HttpSyncPullSmoke.RunAsync(connection);
await HttpSyncPushSmoke.RunAsync(connection);
await HttpSyncBootstrapSmoke.RunAsync(connection);
await HttpRecurrenceSmoke.RunAsync(connection);
await HttpOccurrenceCompleteSmoke.RunAsync(connection);
await HttpAfterCompletionSmoke.RunAsync(connection);
await HttpAiParseSmoke.RunAsync(connection);
await HttpBreakdownSmoke.RunAsync(connection);
