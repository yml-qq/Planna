using System.Text.Json.Serialization;

namespace Planna.Application.Ai;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalBreakdownRequest(
    [property: JsonPropertyName("guidance")] string? Guidance);

public sealed record GoalBreakdownSuggestion(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes,
    [property: JsonPropertyName("position")] int Position);

public sealed record GoalBreakdownPreview(
    [property: JsonPropertyName("goal_id")] Guid GoalId,
    [property: JsonPropertyName("suggestions")] IReadOnlyList<GoalBreakdownSuggestion> Suggestions,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions);
