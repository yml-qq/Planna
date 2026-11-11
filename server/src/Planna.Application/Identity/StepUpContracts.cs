using System.Text.Json.Serialization;

namespace Planna.Application.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StepUpVerifyRequest(
    [property: JsonPropertyName("purpose")] string? Purpose,
    [property: JsonPropertyName("code")] string? Code);

public sealed record StepUpChallengeResponse(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record StepUpVerifyResponse(
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public enum StepUpStatus { Accepted, Success, Invalid, RateLimited, AccountDeleted }

public sealed record StepUpResult(StepUpStatus Status, DateTimeOffset? ExpiresAt = null,
    string? ConfirmationToken = null, int? RetryAfterSeconds = null);

public interface IStepUpService
{
    Task<StepUpResult> ChallengeAsync(Guid ownerId, Guid requestKey, CancellationToken ct);

    Task<StepUpResult> VerifyAsync(Guid ownerId, string? purpose, string? code, CancellationToken ct);

    bool TryValidateToken(Guid ownerId, string? purpose, string? token);
}
