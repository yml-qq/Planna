using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Planna.Application.Export;

namespace Planna.Infrastructure.Export;

// Cloud export: builds one repeatable-read snapshot of committed data and stores it for 24h.
// Generation is synchronous for the MVP; an asynchronous worker can replace it later behind the same contract.
public sealed class AccountExportService(string connectionString, TimeProvider clock) : IAccountExportService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<AccountExportResult> CreateAsync(Guid ownerId, Guid mutationId, CancellationToken ct)
    {
        var requestHash = Hash("POST /me/exports");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var previous = await connection.QuerySingleOrDefaultAsync<MutationRow>(new CommandDefinition(
            "SELECT request_hash AS \"RequestHash\", response::text AS \"Response\" " +
            "FROM client_mutations WHERE owner_id=@Owner AND mutation_id=@Mutation",
            new { Owner = ownerId, Mutation = mutationId }, transaction, cancellationToken: ct));
        if (previous is not null)
        {
            if (previous.RequestHash != requestHash)
                return new(AccountExportStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
            var job = JsonSerializer.Deserialize<AccountExportJobView>(previous.Response);
            return job is not null
                ? new(AccountExportStatus.Replayed, Job: job)
                : new(AccountExportStatus.Conflict, Code: "IDEMPOTENCY_KEY_REUSED");
        }

        var now = clock.GetUtcNow();
        var exportId = Guid.NewGuid();
        var payload = await BuildPayloadAsync(connection, transaction, ownerId, now, ct);
        var expiresAt = now.Add(Lifetime);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO account_exports(id, owner_id, status, snapshot_at, storage_key, requested_at, ready_at, expires_at, payload)
                VALUES(@id, @owner, 'ready', @snapshot, 'cloud', @requested, @ready, @expires, @payload)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", exportId));
            command.Parameters.Add(new NpgsqlParameter("owner", ownerId));
            command.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            command.Parameters.Add(new NpgsqlParameter("requested", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            command.Parameters.Add(new NpgsqlParameter("ready", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
            await command.ExecuteNonQueryAsync(ct);
        }
        var jobView = new AccountExportJobView(exportId, "ready", now, now, expiresAt, null);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO client_mutations(owner_id, mutation_id, request_hash, response, created_at) VALUES(@Owner, @Mutation, @Hash, @Response::jsonb, @Now)",
            new { Owner = ownerId, Mutation = mutationId, Hash = requestHash, Response = JsonSerializer.Serialize(jobView), Now = now.UtcDateTime },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new(AccountExportStatus.Created, Job: jobView);
    }

    public async Task<AccountExportResult> ListAsync(Guid ownerId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<JobRow>(new CommandDefinition("""
            SELECT id AS "Id", status AS "Status", requested_at AS "RequestedAt", snapshot_at AS "SnapshotAt",
                expires_at AS "ExpiresAt", error_code AS "ErrorCode"
            FROM account_exports WHERE owner_id = @OwnerId ORDER BY requested_at DESC LIMIT 100
            """, new { OwnerId = ownerId }, cancellationToken: ct));
        return new(AccountExportStatus.Listed, Jobs: new AccountExportJobList(rows.Select(ToView).ToList()));
    }

    public async Task<AccountExportResult> GetAsync(Guid ownerId, Guid exportId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
            SELECT id AS "Id", status AS "Status", requested_at AS "RequestedAt", snapshot_at AS "SnapshotAt",
                expires_at AS "ExpiresAt", error_code AS "ErrorCode"
            FROM account_exports WHERE id = @Id AND owner_id = @OwnerId
            """, new { Id = exportId, OwnerId = ownerId }, cancellationToken: ct));
        return row is null
            ? new(AccountExportStatus.NotFound, Code: "EXPORT_NOT_FOUND")
            : new(AccountExportStatus.Ok, Job: ToView(row));
    }

    public async Task<AccountExportResult> DownloadAsync(Guid ownerId, Guid exportId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<DownloadRow>(new CommandDefinition("""
            SELECT status AS "Status", expires_at AS "ExpiresAt", payload::text AS "Payload"
            FROM account_exports WHERE id = @Id AND owner_id = @OwnerId
            """, new { Id = exportId, OwnerId = ownerId }, cancellationToken: ct));
        if (row is null) return new(AccountExportStatus.NotFound, Code: "EXPORT_NOT_FOUND");
        if (row.Status != "ready" || string.IsNullOrEmpty(row.Payload))
            return new(AccountExportStatus.NotReady, Code: "EXPORT_NOT_READY");
        if (row.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
            return new(AccountExportStatus.Expired, Code: "EXPORT_EXPIRED");
        return new(AccountExportStatus.Ok, Payload: row.Payload);
    }

    private static async Task<string> BuildPayloadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid ownerId, DateTimeOffset now, CancellationToken ct)
    {
        var account = await connection.QuerySingleAsync<AccountRow>(new CommandDefinition(
            "SELECT id AS \"Id\", timezone AS \"TimeZone\" FROM users WHERE id=@Owner",
            new { Owner = ownerId }, transaction, cancellationToken: ct));
        var lists = (await connection.QueryAsync<ListRow>(new CommandDefinition("""
            SELECT id AS "Id", name AS "Name", kind AS "Kind", version AS "Version"
            FROM task_lists WHERE owner_id=@Owner AND deleted_at IS NULL ORDER BY sort_order, id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var tasks = (await connection.QueryAsync<TaskRow>(new CommandDefinition("""
            SELECT id AS "Id", list_id AS "ListId", title AS "Title", description AS "Description",
                status AS "Status", priority AS "Priority", start_at AS "StartAt", due_at AS "DueAt",
                due_local_date AS "DueLocalDate", all_day AS "AllDay", time_zone AS "TimeZone",
                estimate_minutes AS "EstimateMinutes", completed_at AS "CompletedAt", version AS "Version"
            FROM tasks WHERE owner_id=@Owner AND deleted_at IS NULL ORDER BY created_at, id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var steps = (await connection.QueryAsync<StepRow>(new CommandDefinition("""
            SELECT s.id AS "Id", s.task_id AS "TaskId", s.title AS "Title", s.is_done AS "IsDone",
                s.estimate_minutes AS "EstimateMinutes"
            FROM task_steps s JOIN tasks t ON t.owner_id = s.owner_id AND t.id = s.task_id
            WHERE s.owner_id=@Owner AND s.deleted_at IS NULL AND t.deleted_at IS NULL
            ORDER BY s.task_id, s.position, s.id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var tags = (await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT id AS "Id", name AS "Name" FROM tags
            WHERE owner_id=@Owner AND deleted_at IS NULL ORDER BY lower(name), id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var reminders = (await connection.QueryAsync<ReminderRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", scheduled_at AS "ScheduledAt", enabled AS "Enabled"
            FROM reminders WHERE owner_id=@Owner AND deleted_at IS NULL ORDER BY scheduled_at, id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var blocks = (await connection.QueryAsync<BlockRow>(new CommandDefinition("""
            SELECT id AS "Id", task_id AS "TaskId", start_at AS "StartAt", end_at AS "EndAt"
            FROM task_time_blocks WHERE owner_id=@Owner AND deleted_at IS NULL ORDER BY start_at, id
            """, new { Owner = ownerId }, transaction, cancellationToken: ct))).ToList();
        var scopes = await connection.ExecuteScalarAsync<string[]?>(new CommandDefinition(
            "SELECT allowed_scopes FROM ai_consents WHERE owner_id=@Owner",
            new { Owner = ownerId }, transaction, cancellationToken: ct));

        var data = new JsonObject
        {
            ["account"] = new JsonObject
            {
                ["id"] = account.Id,
                ["time_zone"] = account.TimeZone
            },
            ["lists"] = new JsonArray(lists.Select(list => (JsonNode)new JsonObject
            {
                ["id"] = list.Id,
                ["name"] = list.Name,
                ["kind"] = list.Kind,
                ["version"] = list.Version
            }).ToArray()),
            ["tasks"] = new JsonArray(tasks.Select(task => (JsonNode)new JsonObject
            {
                ["id"] = task.Id,
                ["list_id"] = task.ListId,
                ["title"] = task.Title,
                ["description"] = task.Description,
                ["status"] = task.Status,
                ["priority"] = task.Priority,
                ["start_at"] = Instant(task.StartAt),
                ["due_at"] = Instant(task.DueAt),
                ["due_local_date"] = task.DueLocalDate?.ToString("yyyy-MM-dd"),
                ["all_day"] = task.AllDay,
                ["time_zone"] = task.TimeZone,
                ["estimate_minutes"] = task.EstimateMinutes is null ? null : JsonValue.Create(task.EstimateMinutes.Value),
                ["completed_at"] = Instant(task.CompletedAt),
                ["version"] = task.Version,
                ["deleted_at"] = null
            }).ToArray()),
            ["steps"] = new JsonArray(steps.Select(step => (JsonNode)new JsonObject
            {
                ["id"] = step.Id,
                ["task_id"] = step.TaskId,
                ["title"] = step.Title,
                ["is_done"] = step.IsDone,
                ["estimate_minutes"] = step.EstimateMinutes is null ? null : JsonValue.Create(step.EstimateMinutes.Value)
            }).ToArray()),
            ["tags"] = new JsonArray(tags.Select(tag => (JsonNode)new JsonObject
            {
                ["id"] = tag.Id,
                ["name"] = tag.Name
            }).ToArray()),
            ["reminders"] = new JsonArray(reminders.Select(reminder => (JsonNode)new JsonObject
            {
                ["id"] = reminder.Id,
                ["task_id"] = reminder.TaskId,
                ["scheduled_at"] = Instant(reminder.ScheduledAt),
                ["enabled"] = reminder.Enabled
            }).ToArray()),
            ["time_blocks"] = new JsonArray(blocks.Select(block => (JsonNode)new JsonObject
            {
                ["id"] = block.Id,
                ["task_id"] = block.TaskId,
                ["start_at"] = Instant(block.StartAt),
                ["end_at"] = Instant(block.EndAt)
            }).ToArray()),
            ["ai_consents"] = scopes is { Length: > 0 }
                ? new JsonArray(new JsonObject
                {
                    ["allowed_scopes"] = new JsonArray(scopes.Select(scope => (JsonNode)scope).ToArray())
                })
                : new JsonArray(),
            ["ai_memories"] = new JsonArray()
        };
        var export = new JsonObject
        {
            ["schema_version"] = "account-export-v1",
            ["source"] = "cloud",
            ["generated_at"] = now,
            ["snapshot_at"] = now,
            ["pending_mutation_ids"] = new JsonArray(),
            ["data"] = data
        };
        return export.ToJsonString();
    }

    private static JsonNode? Instant(DateTime? value) =>
        value is null ? null : JsonValue.Create(new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static AccountExportJobView ToView(JobRow row) => new(row.Id, row.Status,
        new DateTimeOffset(DateTime.SpecifyKind(row.RequestedAt, DateTimeKind.Utc)),
        row.SnapshotAt is { } snapshot ? new DateTimeOffset(DateTime.SpecifyKind(snapshot, DateTimeKind.Utc)) : null,
        row.ExpiresAt is { } expires ? new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)) : null,
        row.ErrorCode);

    private sealed class AccountRow
    {
        public Guid Id { get; set; }
        public string TimeZone { get; set; } = "Asia/Shanghai";
    }

    private sealed class MutationRow
    {
        public string RequestHash { get; set; } = "";
        public string Response { get; set; } = "";
    }

    private sealed class JobRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "pending";
        public DateTime RequestedAt { get; set; }
        public DateTime? SnapshotAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? ErrorCode { get; set; }
    }

    private sealed class DownloadRow
    {
        public string Status { get; set; } = "pending";
        public DateTime? ExpiresAt { get; set; }
        public string? Payload { get; set; }
    }

    private sealed class ListRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "custom";
        public long Version { get; set; }
    }

    private sealed class TaskRow
    {
        public Guid Id { get; set; }
        public Guid ListId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "todo";
        public short Priority { get; set; }
        public DateTime? StartAt { get; set; }
        public DateTime? DueAt { get; set; }
        public DateOnly? DueLocalDate { get; set; }
        public bool AllDay { get; set; }
        public string TimeZone { get; set; } = "Asia/Shanghai";
        public short? EstimateMinutes { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long Version { get; set; }
    }

    private sealed class StepRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public string Title { get; set; } = "";
        public bool IsDone { get; set; }
        public short? EstimateMinutes { get; set; }
    }

    private sealed class TagRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class ReminderRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public DateTime ScheduledAt { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class BlockRow
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
    }
}
