using System.Text.Json.Serialization;

namespace Planna.Application.Reminders;

public sealed record ReminderView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("occurrence_id")] Guid? OccurrenceId,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset ScheduledAt,
    [property: JsonPropertyName("offset_minutes")] int? OffsetMinutes,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("version")] long Version);

public sealed record ReminderList([property: JsonPropertyName("items")] IReadOnlyList<ReminderView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateReminderRequest(
    [property: JsonPropertyName("occurrence_id")] Guid? OccurrenceId,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt,
    [property: JsonPropertyName("offset_minutes")] int? OffsetMinutes,
    [property: JsonPropertyName("channel")] string? Channel);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReminderPatch(
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt,
    [property: JsonPropertyName("offset_minutes")] int? OffsetMinutes,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("enabled")] bool? Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateReminderRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] ReminderPatch? Patch);

public enum ReminderStatus
{
    Ok, Created, Updated, Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record ReminderResult(ReminderStatus Status, ReminderView? Reminder = null,
    ReminderList? Reminders = null, string? Code = null, object? Details = null);

public interface IReminderService
{
    Task<ReminderResult> ListAsync(Guid ownerId, Guid taskId, bool includeDisabled,
        CancellationToken ct);

    Task<ReminderResult> UpcomingAsync(Guid ownerId, DateTimeOffset from, DateTimeOffset to,
        int limit, CancellationToken ct);

    Task<ReminderResult> CreateAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateReminderRequest request, CancellationToken ct);

    Task<ReminderResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid taskId, Guid reminderId,
        UpdateReminderRequest request, CancellationToken ct);

    Task<ReminderResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid taskId, Guid reminderId,
        long expectedVersion, CancellationToken ct);
}
