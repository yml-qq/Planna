using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

public sealed record TaskListView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("version")] long Version);

public sealed record TaskListsView(
    [property: JsonPropertyName("items")] IReadOnlyList<TaskListView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskListRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskListPatch(
    [property: JsonPropertyName("name")] string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateTaskListRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("patch")] TaskListPatch Patch);

public enum CreateTaskListStatus { Created, Replayed, Invalid, Conflict, AccountDeleted }

public sealed record CreateTaskListResult(CreateTaskListStatus Status, TaskListView? List = null, string? Code = null);

public interface ICreateTaskList
{
    Task<CreateTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateTaskListRequest request, CancellationToken ct);
}

public enum UpdateTaskListStatus { Updated, Replayed, Invalid, NotFound, VersionConflict, Conflict, AccountDeleted }

public sealed record UpdateTaskListResult(UpdateTaskListStatus Status, TaskListView? List = null,
    string? Code = null, VersionConflictDetails? Details = null);

public interface IUpdateTaskList
{
    Task<UpdateTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid listId,
        UpdateTaskListRequest request, CancellationToken ct);
}

public enum DeleteTaskListStatus
{
    Deleted, Replayed, Invalid, NotFound, VersionConflict, NotEmpty, InboxProtected,
    Conflict, AccountDeleted
}

public sealed record DeleteTaskListResult(DeleteTaskListStatus Status, string? Code = null,
    VersionConflictDetails? Details = null);

public interface IDeleteTaskList
{
    Task<DeleteTaskListResult> ExecuteAsync(Guid ownerId, Guid mutationId, Guid listId,
        long expectedVersion, CancellationToken ct);
}

public enum GetTaskListStatus { Ok, NotFound }

public sealed record GetTaskListResult(GetTaskListStatus Status, TaskListView? List = null);

public interface ITaskListReadQuery
{
    Task<IReadOnlyList<TaskListView>> ListAsync(Guid ownerId, CancellationToken ct);

    Task<GetTaskListResult> GetAsync(Guid ownerId, Guid listId, CancellationToken ct);
}
