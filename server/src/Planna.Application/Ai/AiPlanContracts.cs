using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Ai;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposePlanRequest(
    [property: JsonPropertyName("task_ids")] IReadOnlyList<Guid>? TaskIds,
    [property: JsonPropertyName("range_start")] DateTimeOffset RangeStart,
    [property: JsonPropertyName("range_end")] DateTimeOffset RangeEnd);

public sealed record PlanAfterView(
    [property: JsonPropertyName("start_at")] DateTimeOffset StartAt,
    [property: JsonPropertyName("end_at")] DateTimeOffset EndAt);

public sealed record PlanOperationView(
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("before")] JsonElement? Before,
    [property: JsonPropertyName("after")] PlanAfterView After,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record AiPlanView(
    [property: JsonPropertyName("plan_id")] Guid PlanId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken,
    [property: JsonPropertyName("operations")] IReadOnlyList<PlanOperationView> Operations,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string> Conflicts,
    [property: JsonPropertyName("unplanned_task_ids")] IReadOnlyList<Guid> UnplannedTaskIds,
    [property: JsonPropertyName("explanation")] string Explanation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyPlanRequest(
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("accepted_operation_ids")] IReadOnlyList<Guid> AcceptedOperationIds,
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken);

public sealed record ApplyPlanResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("applied_operation_ids")] IReadOnlyList<Guid> AppliedOperationIds);

public enum PlanStatus { Created, Applied, Replayed, Invalid, TaskNotFound, NotFound, Stale, Conflict, AccountDeleted }

public sealed record PlanResult(PlanStatus Status, AiPlanView? Plan = null,
    ApplyPlanResponse? Applied = null, string? Code = null);

public interface IProposePlan
{
    Task<PlanResult> ExecuteAsync(Guid ownerId, ProposePlanRequest request, CancellationToken ct);
}

public interface IApplyPlan
{
    Task<PlanResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid planId,
        ApplyPlanRequest request, CancellationToken ct);
}

public sealed record SchedulableTask(Guid Id, long Version, string Title, short? EstimateMinutes);

public sealed record ExistingBlock(DateTimeOffset Start, DateTimeOffset End);

public sealed record PlanSchedule(
    IReadOnlyList<PlanOperationView> Operations,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<Guid> UnplannedTaskIds,
    string Explanation);

public interface IPlanScheduler
{
    PlanSchedule Build(IReadOnlyList<SchedulableTask> tasks, DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd, IReadOnlyList<ExistingBlock> existing);
}
