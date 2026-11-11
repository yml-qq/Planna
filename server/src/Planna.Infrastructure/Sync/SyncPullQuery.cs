using System.Text.Json;
using Dapper;
using Npgsql;
using Planna.Application.Sync;

namespace Planna.Infrastructure.Sync;

// Read model only. Change rows are written by the business write services inside their transactions.
public sealed class SyncPullQuery(string connectionString, TimeProvider clock) : ISyncPullQuery
{
    private const string ChangeSql = """
        SELECT revision AS "Revision", change_index AS "ChangeIndex", entity_type AS "EntityType",
            entity_id AS "EntityId", op AS "Op", entity_version AS "Version", payload::text AS "Payload"
        FROM sync_changes
        WHERE owner_id = @OwnerId AND (revision, change_index) > (CAST(@Revision AS bigint), CAST(@ChangeIndex AS int))
        ORDER BY revision, change_index
        LIMIT @Take
        """;

    public async Task<SyncPullResult> PullAsync(Guid ownerId, SyncPageCursor? cursor, int limit, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var minAvailable = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT min_available_revision FROM owner_sync_clocks WHERE owner_id = @OwnerId",
            new { OwnerId = ownerId }, cancellationToken: ct)) ?? 0L;
        var from = cursor ?? new SyncPageCursor(0, 0);
        if (from.Revision < minAvailable) return new(SyncPullStatus.CursorExpired);

        var rows = (await connection.QueryAsync<SyncChangeDbRow>(new CommandDefinition(ChangeSql, new
        {
            OwnerId = ownerId,
            Revision = from.Revision,
            ChangeIndex = from.ChangeIndex,
            Take = limit + 1
        }, cancellationToken: ct))).AsList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var changes = rows.Select(row => new SyncChangeView(row.Revision, row.ChangeIndex, row.EntityType,
            row.EntityId, row.Op, row.Version, ParsePayload(row.Payload))).ToList();
        var last = rows.Count > 0 ? new SyncPageCursor(rows[^1].Revision, rows[^1].ChangeIndex) : from;
        return new(SyncPullStatus.Ok, new SyncPullResponse(changes, last.Encode(), hasMore, clock.GetUtcNow()));
    }

    private static JsonElement? ParsePayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private sealed class SyncChangeDbRow
    {
        public long Revision { get; set; }
        public int ChangeIndex { get; set; }
        public string EntityType { get; set; } = "task";
        public Guid EntityId { get; set; }
        public string Op { get; set; } = "upsert";
        public long Version { get; set; }
        public string? Payload { get; set; }
    }
}
