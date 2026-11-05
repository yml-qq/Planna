using System.Net;
using System.Text.Json.Serialization;

namespace Planna.Application.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginChallengeRequest(
    [property: JsonPropertyName("email")] string Email);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("platform")] string Platform);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed record AuthResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public enum AuthStatus { Success, Accepted, Invalid, RateLimited }
public sealed record AuthResult(AuthStatus Status, AuthResponse? Tokens = null, int? RetryAfterSeconds = null);

public interface IAuthService
{
    Task<AuthResult> ChallengeAsync(string email, Guid requestKey, Guid deviceId,
        IPAddress? remoteAddress, CancellationToken ct);
    Task<AuthResult> LoginAsync(LoginRequest request, Guid deviceId,
        IPAddress? remoteAddress, CancellationToken ct);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct);
    Task<bool> SessionActiveAsync(Guid ownerId, Guid sessionId, CancellationToken ct);
}

public interface ILoginCodeSender
{
    Task SendAsync(string email, string code, CancellationToken ct);
}

public interface IAccessTokenIssuer
{
    string Issue(Guid ownerId, Guid sessionId);
}
