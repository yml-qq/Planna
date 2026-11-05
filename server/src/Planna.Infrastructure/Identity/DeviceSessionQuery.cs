using Dapper;
using Npgsql;
using Planna.Application.Identity;

namespace Planna.Infrastructure.Identity;

// Read model only. Device session mutations use EF Core in RevokeDeviceSession.
public sealed class DeviceSessionQuery(string connectionString) : IDeviceSessionQuery
{
    private const string Sql = """
        SELECT id AS "Id", device_name AS "DeviceName", platform AS "Platform",
            version AS "Version", created_at AS "CreatedAt",
            (id = @CurrentSessionId) AS "IsCurrent"
        FROM device_sessions
        WHERE user_id = @OwnerId AND revoked_at IS NULL
            AND (CAST(@BeforeCreatedAt AS timestamptz) IS NULL OR
                 (created_at, id) < (CAST(@BeforeCreatedAt AS timestamptz), CAST(@BeforeId AS uuid)))
        ORDER BY created_at DESC, id DESC
        LIMIT @Take
        """;

    public async Task<DeviceSessionPage> ListAsync(Guid ownerId, Guid currentSessionId,
        DevicePageCursor? cursor, int limit, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = (await connection.QueryAsync<DeviceSessionDbRow>(new CommandDefinition(Sql, new
        {
            OwnerId = ownerId,
            CurrentSessionId = currentSessionId,
            BeforeCreatedAt = cursor?.CreatedAt,
            BeforeId = cursor?.Id,
            Take = limit + 1
        }, cancellationToken: ct))).AsList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var devices = rows.Select(row => new DeviceSessionView(row.Id, row.DeviceName, row.Platform,
            row.Version, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            row.IsCurrent)).ToList();
        var next = hasMore ? new DevicePageCursor(devices[^1].CreatedAt, devices[^1].Id).Encode() : null;
        return new DeviceSessionPage(devices, next);
    }

    private sealed class DeviceSessionDbRow
    {
        public Guid Id { get; set; }
        public string DeviceName { get; set; } = "";
        public string Platform { get; set; } = "";
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsCurrent { get; set; }
    }
}
