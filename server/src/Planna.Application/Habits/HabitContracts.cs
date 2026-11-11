using System.Text.Json.Serialization;
using Planna.Application.Tasks;

namespace Planna.Application.Habits;

public sealed record HabitView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("schedule_type")] string ScheduleType,
    [property: JsonPropertyName("weekdays")] IReadOnlyList<short>? Weekdays,
    [property: JsonPropertyName("target_quantity")] int TargetQuantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("paused_at")] DateTimeOffset? PausedAt,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHabitRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("schedule_type")] string? ScheduleType,
    [property: JsonPropertyName("weekdays")] IReadOnlyList<short>? Weekdays,
    [property: JsonPropertyName("target_quantity")] int? TargetQuantity,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("time_zone")] string? TimeZone);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HabitPatch(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("schedule_type")] string? ScheduleType,
    [property: JsonPropertyName("weekdays")] IReadOnlyList<short>? Weekdays,
    [property: JsonPropertyName("target_quantity")] int? TargetQuantity,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("time_zone")] string? TimeZone,
    [property: JsonPropertyName("paused")] bool? Paused);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateHabitRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] HabitPatch? Patch);

public sealed record HabitList([property: JsonPropertyName("items")] IReadOnlyList<HabitView> Items);

public enum HabitStatus
{
    Ok, Created, Updated, Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record HabitResult(HabitStatus Status, HabitView? Habit = null, HabitList? Habits = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IHabitCreate
{
    Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateHabitRequest request,
        CancellationToken ct);
}

public interface IHabitUpdate
{
    Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId,
        UpdateHabitRequest request, CancellationToken ct);
}

public interface IHabitDelete
{
    Task<HabitResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId, long expectedVersion,
        CancellationToken ct);
}

public interface IHabitQuery
{
    Task<HabitResult> ListAsync(Guid ownerId, CancellationToken ct);

    Task<HabitResult> GetAsync(Guid ownerId, Guid habitId, CancellationToken ct);
}
