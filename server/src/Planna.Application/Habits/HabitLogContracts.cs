using System.Text.Json.Serialization;

namespace Planna.Application.Habits;

public sealed record HabitLogView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("habit_id")] Guid HabitId,
    [property: JsonPropertyName("log_date")] DateOnly LogDate,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpsertHabitLogRequest(
    [property: JsonPropertyName("quantity")] int? Quantity,
    [property: JsonPropertyName("status")] string? Status);

public sealed record HabitLogList([property: JsonPropertyName("items")] IReadOnlyList<HabitLogView> Items);

public enum HabitLogStatus { Ok, Upserted, Replayed, Invalid, NotFound, Conflict, AccountDeleted }

public sealed record HabitLogResult(HabitLogStatus Status, HabitLogView? Log = null,
    HabitLogList? Logs = null, HabitStreakView? Streak = null, string? Code = null);

public interface IHabitLogUpsert
{
    Task<HabitLogResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid habitId, DateOnly logDate,
        UpsertHabitLogRequest request, CancellationToken ct);
}

public interface IHabitLogQuery
{
    Task<HabitLogResult> ListAsync(Guid ownerId, Guid habitId, DateOnly from, DateOnly to,
        CancellationToken ct);
}

public sealed record HabitStreakView(
    [property: JsonPropertyName("habit_id")] Guid HabitId,
    [property: JsonPropertyName("through")] DateOnly Through,
    [property: JsonPropertyName("current_streak")] int CurrentStreak,
    [property: JsonPropertyName("longest_streak")] int LongestStreak,
    [property: JsonPropertyName("target_quantity")] int TargetQuantity);

public interface IHabitStreakQuery
{
    Task<HabitLogResult> GetStreakAsync(Guid ownerId, Guid habitId, DateOnly through, CancellationToken ct);
}
