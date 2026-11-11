using Planna.Application.Identity;

namespace Planna.Api.Identity;

public static class StepUpEndpoints
{
    public static IEndpointRouteBuilder MapStepUpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Identity").RequireAuthorization();
        group.MapPost("/step-up/challenges", Challenge).WithName("CreateSensitiveActionChallenge");
        group.MapPost("/step-up", Verify).WithName("VerifySensitiveAction");
        return app;
    }

    private static async Task<IResult> Challenge(HttpContext http, IStepUpService stepUp, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                http.User.FindFirst("sub")?.Value, out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var requestKey) || requestKey == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await stepUp.ChallengeAsync(owner, requestKey, ct);
        if (result.Status == StepUpStatus.RateLimited && result.RetryAfterSeconds is { } seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Status == StepUpStatus.Accepted)
            http.Response.Headers["Cache-Control"] = "no-store";
        return result.Status switch
        {
            StepUpStatus.Accepted => TypedResults.Accepted((string?)null,
                new StepUpChallengeResponse("account_delete", result.ExpiresAt!.Value)),
            StepUpStatus.RateLimited => Error(http, 429, "RATE_LIMITED"),
            StepUpStatus.AccountDeleted => Error(http, 401, "ACCOUNT_DELETED"),
            _ => Error(http, 422, "INVALID_STEP_UP_REQUEST")
        };
    }

    private static async Task<IResult> Verify(HttpContext http, StepUpVerifyRequest request,
        IStepUpService stepUp, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                http.User.FindFirst("sub")?.Value, out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await stepUp.VerifyAsync(owner, request.Purpose, request.Code, ct);
        if (result.Status == StepUpStatus.RateLimited && result.RetryAfterSeconds is { } seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Status == StepUpStatus.Success)
            http.Response.Headers["Cache-Control"] = "no-store";
        return result.Status switch
        {
            StepUpStatus.Success => TypedResults.Ok(
                new StepUpVerifyResponse(result.ConfirmationToken!, result.ExpiresAt!.Value)),
            StepUpStatus.RateLimited => Error(http, 429, "RATE_LIMITED"),
            StepUpStatus.AccountDeleted => Error(http, 401, "ACCOUNT_DELETED"),
            _ => Error(http, 401, "INVALID_STEP_UP")
        };
    }

    private static IResult Error(HttpContext http, int status, string code) =>
        TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = status is 429 or 503 }
        }, statusCode: status);
}
