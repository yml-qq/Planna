using System.Text.Json.Serialization;

namespace Planna.Application.Calendar;

public sealed record WorkdayOverrideView(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record WorkdayOverrideList(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkdayOverrideView> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpsertWorkdayOverrideRequest(
    [property: JsonPropertyName("jurisdiction")] string? Jurisdiction,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("note")] string? Note);

public enum WorkdayOverrideStatus
{
    Ok, Created, Updated, Invalid, NotFound
}

public sealed record WorkdayOverrideResult(WorkdayOverrideStatus Status,
    WorkdayOverrideView? Override = null, WorkdayOverrideList? List = null, string? Code = null);

public interface IWorkdayOverrideService
{
    Task<WorkdayOverrideResult> ListAsync(Guid ownerId, string? jurisdiction, DateOnly from,
        DateOnly to, CancellationToken ct);

    Task<WorkdayOverrideResult> UpsertAsync(Guid ownerId, DateOnly date,
        UpsertWorkdayOverrideRequest request, CancellationToken ct);

    Task<WorkdayOverrideResult> DeleteAsync(Guid ownerId, DateOnly date, string? jurisdiction,
        CancellationToken ct);
}
