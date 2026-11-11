using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Sync;

public sealed record SyncChangeView(
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("change_index")] int ChangeIndex,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record SyncPullResponse(
    [property: JsonPropertyName("changes")] IReadOnlyList<SyncChangeView> Changes,
    [property: JsonPropertyName("next_cursor")] string NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime);

// Opaque committed-revision cursor; encodes the last consumed (revision, change_index).
public sealed record SyncPageCursor(long Revision, int ChangeIndex)
{
    public string Encode()
    {
        var value = Revision.ToString(CultureInfo.InvariantCulture) + ":" +
                    ChangeIndex.ToString(CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out SyncPageCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > 100) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var changeIndex) ||
                revision < 0 || changeIndex < 0)
                return false;
            cursor = new SyncPageCursor(revision, changeIndex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public enum SyncPullStatus { Ok, CursorExpired }

public sealed record SyncPullResult(SyncPullStatus Status, SyncPullResponse? Page = null);

public interface ISyncPullQuery
{
    Task<SyncPullResult> PullAsync(Guid ownerId, SyncPageCursor? cursor, int limit, CancellationToken ct);
}
