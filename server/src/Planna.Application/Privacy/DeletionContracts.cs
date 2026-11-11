using System.Text.Json.Serialization;

namespace Planna.Application.Privacy;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteAccountRequest(
    [property: JsonPropertyName("confirmation_token")] string? ConfirmationToken,
    [property: JsonPropertyName("acknowledge_offline_devices")] bool AcknowledgeOfflineDevices);

public sealed record DeleteAccountResponse(
    [property: JsonPropertyName("deletion_request_id")] Guid DeletionRequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("policy_version")] string PolicyVersion);

public enum DeleteAccountStatus
{
    Accepted, Replayed, Invalid, StepUpRequired, AlreadyDeleted, Unavailable, AccountDeleted, Conflict
}

public sealed record DeleteAccountResult(DeleteAccountStatus Status, DeleteAccountResponse? Response = null,
    string? Code = null);

public interface IAccountDeletionService
{
    Task<DeleteAccountResult> ExecuteAsync(Guid ownerId, Guid mutationId, DeleteAccountRequest request,
        CancellationToken ct);
}
