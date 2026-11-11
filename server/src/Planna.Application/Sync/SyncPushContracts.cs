using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planna.Application.Sync;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncMutation(
    [property: JsonPropertyName("mutation_id")] Guid MutationId,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("base_version")] long BaseVersion,
    [property: JsonPropertyName("payload")] JsonElement Payload);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncPushRequest(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("mutations")] IReadOnlyList<SyncMutation> Mutations);

public sealed record SyncPushResult(
    [property: JsonPropertyName("mutation_id")] Guid MutationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("entity_version")] long? EntityVersion,
    [property: JsonPropertyName("server_entity")] JsonElement? ServerEntity,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record SyncPushResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<SyncPushResult> Results);

public interface ISyncPushCommand
{
    Task<SyncPushResponse> ExecuteAsync(Guid ownerId, SyncPushRequest request, CancellationToken ct);
}
