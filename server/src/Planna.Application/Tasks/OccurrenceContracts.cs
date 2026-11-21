using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OccurrencePatch(
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("status")] string? Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateOccurrenceRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] OccurrencePatch? Patch);

public enum PatchOccurrenceStatus
{
    Updated, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted
}

public sealed record PatchOccurrenceResult(PatchOccurrenceStatus Status,
    OccurrenceView? Occurrence = null, string? Code = null, object? Details = null);

public interface IPatchOccurrence
{
    Task<PatchOccurrenceResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        Guid occurrenceId, UpdateOccurrenceRequest request, CancellationToken ct);
}
