using System.Text.Json.Serialization;

namespace Planna.Application.Focus;

public sealed record FocusSessionView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("task_id")] Guid? TaskId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("planned_seconds")] int PlannedSeconds,
    [property: JsonPropertyName("actual_seconds")] int ActualSeconds,
    [property: JsonPropertyName("break_seconds")] int BreakSeconds,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record FocusSessionList([property: JsonPropertyName("items")] IReadOnlyList<FocusSessionView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFocusSessionRequest(
    [property: JsonPropertyName("task_id")] Guid? TaskId,
    [property: JsonPropertyName("planned_seconds")] int? PlannedSeconds,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateFocusSessionRequest(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("actual_seconds")] int? ActualSeconds,
    [property: JsonPropertyName("break_seconds")] int? BreakSeconds,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt);

public sealed record FocusDayStat(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("completed_sessions")] int CompletedSessions,
    [property: JsonPropertyName("actual_seconds")] long ActualSeconds);

public sealed record FocusStats(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("total_sessions")] int TotalSessions,
    [property: JsonPropertyName("completed_sessions")] int CompletedSessions,
    [property: JsonPropertyName("abandoned_sessions")] int AbandonedSessions,
    [property: JsonPropertyName("actual_seconds")] long ActualSeconds,
    [property: JsonPropertyName("days")] IReadOnlyList<FocusDayStat> Days);

public enum FocusStatus
{
    Ok, Created, Updated, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record FocusResult(FocusStatus Status, FocusSessionView? Session = null,
    FocusSessionList? Sessions = null, FocusStats? Stats = null, string? Code = null,
    object? Details = null);

public interface IFocusSessionService
{
    Task<FocusResult> CreateAsync(Guid ownerId, Guid mutationId, CreateFocusSessionRequest request,
        CancellationToken ct);

    Task<FocusResult> ListAsync(Guid ownerId, int limit, CancellationToken ct);

    Task<FocusResult> GetAsync(Guid ownerId, Guid sessionId, CancellationToken ct);

    Task<FocusResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid sessionId, long expectedVersion,
        UpdateFocusSessionRequest request, CancellationToken ct);

    Task<FocusResult> StatsAsync(Guid ownerId, DateOnly from, DateOnly to, string timeZone,
        CancellationToken ct);
}
