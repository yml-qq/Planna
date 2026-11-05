using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Planna.Application.Identity;

public sealed record DeviceSessionView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("is_current")] bool IsCurrent);

public sealed record DeviceSessionPage(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceSessionView> Devices,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record DevicePageCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode()
    {
        var value = CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + Id.ToString("N");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out DevicePageCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > 100) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(parts[1], "N", out var id) || id == Guid.Empty)
                return false;
            cursor = new DevicePageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public interface IDeviceSessionQuery
{
    Task<DeviceSessionPage> ListAsync(Guid ownerId, Guid currentSessionId,
        DevicePageCursor? cursor, int limit, CancellationToken ct);
}

public enum RevokeDeviceStatus { Revoked, Replayed, NotFound, VersionConflict }

public interface IRevokeDeviceSession
{
    Task<RevokeDeviceStatus> ExecuteAsync(Guid ownerId, Guid sessionId,
        int expectedVersion, Guid requestKey, CancellationToken ct);
}
