using System.Text.Json.Serialization;

namespace Planna.Application.Tasks;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("list_id")] Guid ListId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("priority")] short Priority,
    [property: JsonPropertyName("start_at")] DateTimeOffset? StartAt,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("due_local_date")] DateOnly? DueLocalDate,
    [property: JsonPropertyName("all_day")] bool AllDay,
    [property: JsonPropertyName("time_zone")] string? TimeZone,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes);

public sealed record TaskResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("list_id")] Guid ListId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("priority")] short Priority,
    [property: JsonPropertyName("start_at")] DateTimeOffset? StartAt,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
    [property: JsonPropertyName("due_local_date")] DateOnly? DueLocalDate,
    [property: JsonPropertyName("all_day")] bool AllDay,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("estimate_minutes")] short? EstimateMinutes,
    [property: JsonPropertyName("version")] long Version);

public enum CreateTaskStatus { Created, Replayed, Invalid, ListNotFound, Conflict, AccountDeleted }

public sealed record CreateTaskResult(CreateTaskStatus Status, TaskResponse? Task = null, string? Code = null);

public interface ICreateTask
{
    Task<CreateTaskResult> ExecuteAsync(Guid ownerId, Guid mutationId, CreateTaskRequest request, CancellationToken ct);
}
