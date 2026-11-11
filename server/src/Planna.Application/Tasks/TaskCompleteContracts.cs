using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteTaskRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("occurrence_id")] Guid? OccurrenceId,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReopenTaskRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion);

public enum CompleteTaskStatus
{
    Completed, Replayed, Invalid, NotFound, VersionConflict, AlreadyCompleted,
    InvalidState, RecurringTask, InvalidOccurrence, Conflict, AccountDeleted
}

public sealed record CompleteTaskResult(CompleteTaskStatus Status, TaskReadView? Task = null,
    string? Code = null, VersionConflictDetails? Details = null, OccurrenceView? Occurrence = null);

public interface ICompleteTask
{
    Task<CompleteTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        CompleteTaskRequest request, CancellationToken ct);
}

public enum ReopenTaskStatus
{
    Reopened, Replayed, Invalid, NotFound, VersionConflict, NotCompleted,
    InvalidState, Conflict, AccountDeleted
}

public sealed record ReopenTaskResult(ReopenTaskStatus Status, TaskReadView? Task = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IReopenTask
{
    Task<ReopenTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        ReopenTaskRequest request, CancellationToken ct);
}
