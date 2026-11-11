using System.Security.Claims;
using Planna.Application.Ai;

namespace Planna.Api.Ai;

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ai").WithTags("AI").RequireAuthorization();
        group.MapPost("/parse", Parse).WithName("ParseNaturalLanguage");
        return app;
    }

    private static async Task<IResult> Parse(HttpContext http, AiParseRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var provider = http.RequestServices.GetService<IAiParseProvider>();
        if (provider is null) return Error(http, 503, "AI_UNAVAILABLE");
        if (request.Context is null || request.Context.Now is null ||
            string.IsNullOrWhiteSpace(request.Context.TimeZone))
            return Error(http, 400, "INVALID_REQUEST");
        var text = request.Input?.Trim() ?? "";
        if (text.Length is < 1 or > 10000) return Error(http, 400, "INVALID_REQUEST");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.Context.TimeZone); }
        catch (TimeZoneNotFoundException) { return Error(http, 400, "INVALID_TIME_ZONE"); }
        catch (InvalidTimeZoneException) { return Error(http, 400, "INVALID_TIME_ZONE"); }
        if (request.AllowedScopes is { Count: > 0 } scopes && !scopes.Contains("task_drafts"))
            return Error(http, 403, "AI_SCOPE_DENIED");

        var result = await provider.ParseAsync(
            new AiParseInput(text, request.Context.Now.Value, request.Context.TimeZone), ct);
        return TypedResults.Ok(result);
    }

    private static IResult Error(HttpContext http, int status, string code, object? details = null)
    {
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = code,
            ["retryable"] = false
        };
        if (details is not null) error["details"] = details;
        return TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error
        }, statusCode: status);
    }
}
