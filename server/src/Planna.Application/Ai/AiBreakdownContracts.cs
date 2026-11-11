using System.Text.Json.Serialization;
using Planna.Application.Tasks;

namespace Planna.Application.Ai;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateBreakdownRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("guidance")] string? Guidance);

public sealed record BreakdownStepView(
    [property: JsonPropertyName("step_id")] Guid StepId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes,
    [property: JsonPropertyName("position")] int Position);

public sealed record AiBreakdownDraftView(
    [property: JsonPropertyName("draft_id")] Guid DraftId,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("task_version")] long TaskVersion,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken,
    [property: JsonPropertyName("steps")] IReadOnlyList<BreakdownStepView> Steps,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditBreakdownRequest(
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("steps")] IReadOnlyList<BreakdownStepView> Steps);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyBreakdownRequest(
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken);

public sealed record ApplyBreakdownResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("step_ids")] IReadOnlyList<Guid> StepIds);

public enum BreakdownStatus
{
    Created, Edited, Applied, Replayed, Invalid, NotFound, Stale, VersionConflict, StepConflict, Conflict, AccountDeleted
}

public sealed record BreakdownResult(BreakdownStatus Status, AiBreakdownDraftView? Draft = null,
    ApplyBreakdownResponse? Applied = null, string? Code = null, VersionConflictDetails? Details = null);

public interface ICreateBreakdown
{
    Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CreateBreakdownRequest request, CancellationToken ct);
}

public interface IEditBreakdown
{
    Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid draftId,
        EditBreakdownRequest request, CancellationToken ct);
}

public interface IApplyBreakdown
{
    Task<BreakdownResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid draftId,
        ApplyBreakdownRequest request, CancellationToken ct);
}

// Provider abstraction for step drafting; the first stage is deterministic and never writes data.
public interface IAiBreakdownProvider
{
    IReadOnlyList<BreakdownStepView> Generate(string title, string? guidance);
}
