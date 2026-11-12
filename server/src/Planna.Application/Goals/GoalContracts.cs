using System.Text.Json.Serialization;

namespace Planna.Application.Goals;

public sealed record MilestoneView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("goal_id")] Guid GoalId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("due_date")] string? DueDate,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record GoalProgress(
    [property: JsonPropertyName("completed_milestones")] int CompletedMilestones,
    [property: JsonPropertyName("total_milestones")] int TotalMilestones,
    [property: JsonPropertyName("percent")] int Percent);

public sealed record GoalView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("target_date")] string? TargetDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("progress")] GoalProgress Progress,
    [property: JsonPropertyName("milestones")] IReadOnlyList<MilestoneView> Milestones);

public sealed record GoalList([property: JsonPropertyName("items")] IReadOnlyList<GoalView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGoalRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("target_date")] string? TargetDate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalPatch(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("target_date")] string? TargetDate,
    [property: JsonPropertyName("status")] string? Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateGoalRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] GoalPatch? Patch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateMilestoneRequest(
    [property: JsonPropertyName("goal_id")] Guid? GoalId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("due_date")] string? DueDate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MilestonePatch(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("due_date")] string? DueDate,
    [property: JsonPropertyName("completed")] bool? Completed,
    [property: JsonPropertyName("position")] int? Position);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateMilestoneRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] MilestonePatch? Patch);

public enum GoalStatus
{
    Ok, Created, Updated, Deleted, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record GoalResult(GoalStatus Status, GoalView? Goal = null, GoalList? Goals = null,
    MilestoneView? Milestone = null, string? Code = null, object? Details = null);

public interface IGoalService
{
    Task<GoalResult> ListAsync(Guid ownerId, CancellationToken ct);

    Task<GoalResult> GetAsync(Guid ownerId, Guid goalId, CancellationToken ct);

    Task<GoalResult> CreateAsync(Guid ownerId, Guid mutationId, CreateGoalRequest request,
        CancellationToken ct);

    Task<GoalResult> UpdateAsync(Guid ownerId, Guid mutationId, Guid goalId,
        UpdateGoalRequest request, CancellationToken ct);

    Task<GoalResult> DeleteAsync(Guid ownerId, Guid mutationId, Guid goalId, long expectedVersion,
        CancellationToken ct);

    Task<GoalResult> CreateMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        CreateMilestoneRequest request, CancellationToken ct);

    Task<GoalResult> UpdateMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        Guid milestoneId, UpdateMilestoneRequest request, CancellationToken ct);

    Task<GoalResult> DeleteMilestoneAsync(Guid ownerId, Guid mutationId, Guid goalId,
        Guid milestoneId, long expectedVersion, CancellationToken ct);
}
