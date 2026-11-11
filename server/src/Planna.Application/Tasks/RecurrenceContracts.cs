using System.Globalization;
using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

public sealed record RecurrenceView(
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rrule")] string? Rrule,
    [property: JsonPropertyName("interval_days")] int? IntervalDays,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("dtstart_local")] DateTime DtstartLocal,
    [property: JsonPropertyName("version")] long Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PutRecurrenceRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rrule")] string? Rrule,
    [property: JsonPropertyName("interval_days")] int? IntervalDays,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("dtstart_local")] string DtstartLocal);

public enum PutRecurrenceStatus
{
    Saved, Replayed, Invalid, Unsupported, TaskNotFound, NotFound, Exists,
    VersionConflict, Conflict, AccountDeleted
}

public sealed record PutRecurrenceResult(PutRecurrenceStatus Status, RecurrenceView? Recurrence = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IPutRecurrence
{
    Task<PutRecurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        PutRecurrenceRequest request, CancellationToken ct);
}

public enum DeleteRecurrenceStatus { Deleted, Replayed, Invalid, TaskNotFound, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record DeleteRecurrenceResult(DeleteRecurrenceStatus Status, string? Code = null,
    VersionConflictDetails? Details = null);

public interface IDeleteRecurrence
{
    Task<DeleteRecurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        long expectedVersion, CancellationToken ct);
}

public sealed record OccurrenceView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("original_local_start")] DateTime OriginalLocalStart,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("version")] long Version);

public sealed record OccurrenceListView(
    [property: JsonPropertyName("items")] IReadOnlyList<OccurrenceView> Items);

public enum GetOccurrenceListStatus { Ok, TaskNotFound, RecurrenceNotFound, InvalidWindow }

public sealed record GetOccurrenceListResult(GetOccurrenceListStatus Status,
    IReadOnlyList<OccurrenceView>? Items = null);

public interface IRecurrenceQuery
{
    Task<(GetOccurrenceListStatus Status, RecurrenceView? Recurrence)> GetAsync(Guid ownerId, Guid taskId, CancellationToken ct);

    Task<GetOccurrenceListResult> ListOccurrencesAsync(Guid ownerId, Guid taskId,
        DateOnly from, DateOnly to, CancellationToken ct);
}

public static class RecurrenceFormats
{
    public static bool TryParseLocal(string? value, out DateTime local)
    {
        local = default;
        if (string.IsNullOrEmpty(value)) return false;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss.fffffff"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out local);
    }
}
