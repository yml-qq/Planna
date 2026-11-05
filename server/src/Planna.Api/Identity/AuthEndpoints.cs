using Planna.Application.Identity;

namespace Planna.Api.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Identity");
        group.MapPost("/login/challenges", Challenge).WithName("CreateLoginChallenge");
        group.MapPost("/login", Login).WithName("Login");
        group.MapPost("/refresh", Refresh).WithName("RefreshToken");
        return app;
    }

    private static async Task<IResult> Challenge(
        HttpContext http, LoginChallengeRequest request, IAuthService auth,
        TimeProvider clock, CancellationToken ct)
    {
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var key))
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!Guid.TryParse(http.Request.Headers["X-Device-Id"], out var deviceId) || deviceId == Guid.Empty)
            return Error(http, 400, "INVALID_DEVICE_ID");
        var result = await auth.ChallengeAsync(request.Email, key, deviceId,
            http.Connection.RemoteIpAddress, ct);
        if (result.Status == AuthStatus.RateLimited && result.RetryAfterSeconds is { } seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result.Status switch
        {
            AuthStatus.Accepted => TypedResults.Accepted((string?)null, new
            {
                expires_at = clock.GetUtcNow().AddMinutes(10)
            }),
            AuthStatus.RateLimited => Error(http, 429, "RATE_LIMITED"),
            _ => Error(http, 422, "INVALID_EMAIL")
        };
    }

    private static async Task<IResult> Login(
        HttpContext http, LoginRequest request, IAuthService auth, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, ct);
        return result.Status == AuthStatus.Success
            ? TypedResults.Ok(result.Tokens)
            : Error(http, 401, "INVALID_CODE");
    }

    private static async Task<IResult> Refresh(
        HttpContext http, RefreshRequest request, IAuthService auth, CancellationToken ct)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, ct);
        return result.Status == AuthStatus.Success
            ? TypedResults.Ok(result.Tokens)
            : Error(http, 401, "INVALID_REFRESH_TOKEN");
    }

    private static IResult Error(HttpContext http, int status, string code) =>
        TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = status is 429 or 503 }
        }, statusCode: status);
}
