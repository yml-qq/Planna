using System.Security.Claims;
using Planna.Application.Ai;

namespace Planna.Api.Ai;

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ai").WithTags("AI").RequireAuthorization();
        group.MapPost("/parse", Parse).WithName("ParseNaturalLanguage");
        group.MapPost("/tasks/{taskId}/breakdowns", CreateBreakdown).WithName("CreateBreakdown");
        group.MapPut("/breakdowns/{draftId}", EditBreakdown).WithName("EditBreakdown");
        group.MapPost("/breakdowns/{draftId}/apply", ApplyBreakdown).WithName("ApplyBreakdown");
        return app;
    }

    private static async Task<IResult> CreateBreakdown(HttpContext http, string taskId,
        CreateBreakdownRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(taskId, out var id) || id == Guid.Empty) return Error(http, 400, "INVALID_TASK_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (http.RequestServices.GetService<IAiBreakdownProvider>() is null)
            return Error(http, 503, "AI_UNAVAILABLE");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var command = http.RequestServices.GetRequiredService<ICreateBreakdown>();
        var result = await command.ExecuteAsync(owner, mutation, id, request, ct);
        return result.Status is BreakdownStatus.Created or BreakdownStatus.Replayed
            ? TypedResults.Created($"/ai/breakdowns/{result.Draft!.DraftId}", result.Draft)
            : MapBreakdownResult(http, result);
    }

    private static async Task<IResult> EditBreakdown(HttpContext http, string draftId,
        EditBreakdownRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(draftId, out var id) || id == Guid.Empty) return Error(http, 400, "INVALID_BREAKDOWN_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (http.RequestServices.GetService<IAiBreakdownProvider>() is null)
            return Error(http, 503, "AI_UNAVAILABLE");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var command = http.RequestServices.GetRequiredService<IEditBreakdown>();
        var result = await command.ExecuteAsync(owner, mutation, id, request, ct);
        return result.Status is BreakdownStatus.Edited or BreakdownStatus.Replayed
            ? TypedResults.Ok(result.Draft)
            : MapBreakdownResult(http, result);
    }

    private static async Task<IResult> ApplyBreakdown(HttpContext http, string draftId,
        ApplyBreakdownRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(draftId, out var id) || id == Guid.Empty) return Error(http, 400, "INVALID_BREAKDOWN_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (http.RequestServices.GetService<IAiBreakdownProvider>() is null)
            return Error(http, 503, "AI_UNAVAILABLE");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var command = http.RequestServices.GetRequiredService<IApplyBreakdown>();
        var result = await command.ExecuteAsync(owner, mutation, id, request, ct);
        return result.Status is BreakdownStatus.Applied or BreakdownStatus.Replayed
            ? TypedResults.Ok(result.Applied)
            : MapBreakdownResult(http, result);
    }

    private static IResult MapBreakdownResult(HttpContext http, BreakdownResult result) => result.Status switch
    {
        BreakdownStatus.Invalid => Error(http, 422, result.Code!),
        BreakdownStatus.NotFound => Error(http, 404, result.Code!),
        BreakdownStatus.Stale => Error(http, 409, "BREAKDOWN_STALE"),
        BreakdownStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
        BreakdownStatus.StepConflict => Error(http, 409, result.Code!),
        BreakdownStatus.Conflict => Error(http, 409, result.Code!),
        BreakdownStatus.AccountDeleted => Error(http, 401, result.Code!),
        _ => Error(http, 500, "INTERNAL_ERROR")
    };

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
