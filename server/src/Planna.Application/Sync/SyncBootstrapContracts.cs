using System.Text.Json.Serialization;
using Planna.Application.Tasks;

namespace Planna.Application.Sync;

public sealed record SyncBootstrapResponse(
    [property: JsonPropertyName("snapshot_cursor")] string SnapshotCursor,
    [property: JsonPropertyName("next_page_token")] string? NextPageToken,
    [property: JsonPropertyName("tasks")] IReadOnlyList<TaskReadView> Tasks,
    [property: JsonPropertyName("lists")] IReadOnlyList<TaskListView> Lists);

public interface ISyncBootstrapQuery
{
    Task<SyncBootstrapResponse> BootstrapAsync(Guid ownerId, CancellationToken ct);
}
