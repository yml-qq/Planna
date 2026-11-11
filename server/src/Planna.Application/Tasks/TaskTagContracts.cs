using System.Text.Json.Serialization;
using Planna.Application.Tags;

namespace Planna.Application.Tasks;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceTaskTagsRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("tag_ids")] IReadOnlyList<Guid> TagIds);

public sealed record TaskTagSetView(
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("tag_ids")] IReadOnlyList<Guid> TagIds);

public enum SetTaskTagsStatus
{
    Updated, Replayed, Invalid, NotFound, VersionConflict, InvalidTagSet,
    Conflict, AccountDeleted
}

public sealed record SetTaskTagsResult(SetTaskTagsStatus Status, TaskTagSetView? Set = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface ISetTaskTags
{
    Task<SetTaskTagsResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid taskId,
        ReplaceTaskTagsRequest request, CancellationToken ct);
}

public enum GetTaskTagsStatus { Ok, TaskNotFound }

public sealed record GetTaskTagsResult(GetTaskTagsStatus Status, IReadOnlyList<TagView>? Items = null);

public interface ITaskTagQuery
{
    Task<GetTaskTagsResult> GetAsync(Guid ownerId, Guid taskId, CancellationToken ct);
}
