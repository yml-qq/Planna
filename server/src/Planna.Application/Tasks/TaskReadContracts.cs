using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

public sealed record TaskReadView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("list_id")] Guid ListId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("priority")] short Priority,
    [property: JsonPropertyName("start_at")] DateTimeOffset? StartAt,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("due_local_date")] DateOnly? DueLocalDate,
    [property: JsonPropertyName("all_day")] bool AllDay,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("version")] long Version);

public sealed record TaskReadPage(
    [property: JsonPropertyName("items")] IReadOnlyList<TaskReadView> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

// Opaque keyset cursor over (created_at, id); same encoding scheme as the device list, typed separately.
public sealed record TaskPageCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode()
    {
        var value = CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + Id.ToString("N");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out TaskPageCursor? cursor)
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
            cursor = new TaskPageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public enum TaskReadStatus { Ok, ListNotFound }

public sealed record TaskReadResult(TaskReadStatus Status, TaskReadPage? Page = null);

public enum TaskGetStatus { Ok, NotFound }

public sealed record GetTaskResult(TaskGetStatus Status, TaskReadView? Task = null);

public interface ITaskReadQuery
{
    Task<TaskReadResult> ListAsync(Guid ownerId, TaskPageCursor? cursor, int limit,
        Guid? listId, string? status, CancellationToken ct);

    Task<GetTaskResult> GetAsync(Guid ownerId, Guid taskId, CancellationToken ct);
}
