using System.Security.Claims;
using Planna.Application.Ai;

namespace Planna.Api.Ai;

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ai").WithTags("AI").RequireAuthorization();
        group.MapPost("/parse", Parse).WithName("ParseNaturalLanguage");
        group.MapGet("/consent", GetConsent).WithName("GetAiConsent");
        group.MapPut("/consent", UpdateConsent).WithName("UpdateAiConsent");
        group.MapPost("/tasks/{taskId}/breakdowns", CreateBreakdown).WithName("CreateBreakdown");
        group.MapPut("/breakdowns/{draftId}", EditBreakdown).WithName("EditBreakdown");
        group.MapPost("/breakdowns/{draftId}/apply", ApplyBreakdown).WithName("ApplyBreakdown");
        group.MapPost("/plans", ProposePlan).WithName("ProposeAiPlan");
        group.MapPost("/plans/{planId}/apply", ApplyPlan).WithName("ApplyAiPlan");
        return app;
    }

    private static async Task<IResult> ProposePlan(HttpContext http, ProposePlanRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (http.RequestServices.GetService<IPlanScheduler>() is null)
            return Error(http, 503, "AI_UNAVAILABLE");
        var command = http.RequestServices.GetRequiredService<IProposePlan>();
        var result = await command.ExecuteAsync(owner, request, ct);
        return result.Status switch
        {
            PlanStatus.Created => TypedResults.Ok(result.Plan),
            PlanStatus.Invalid => Error(http, 422, result.Code!),
            PlanStatus.TaskNotFound => Error(http, 404, result.Code!),
            PlanStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> ApplyPlan(HttpContext http, string planId, ApplyPlanRequest request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(planId, out var id) || id == Guid.Empty) return Error(http, 400, "INVALID_PLAN_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (http.RequestServices.GetService<IPlanScheduler>() is null)
            return Error(http, 503, "AI_UNAVAILABLE");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var command = http.RequestServices.GetRequiredService<IApplyPlan>();
        var result = await command.ExecuteAsync(owner, mutation, id, request, ct);
        return result.Status switch
        {
            PlanStatus.Applied or PlanStatus.Replayed => TypedResults.Ok(result.Applied),
            PlanStatus.Invalid => Error(http, 422, result.Code!),
            PlanStatus.NotFound => Error(http, 404, result.Code!),
            PlanStatus.Stale => Error(http, 409, "PLAN_STALE"),
            PlanStatus.Conflict => Error(http, 409, result.Code!),
            PlanStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
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

    private static async Task<IResult> GetConsent(HttpContext http, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var service = http.RequestServices.GetRequiredService<IAiConsent>();
        var result = await service.GetAsync(owner, ct);
        return result.Status switch
        {
            AiConsentStatus.Ok => TypedResults.Ok(result.View),
            AiConsentStatus.Invalid => Error(http, 400, "INVALID_REQUEST"),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> UpdateConsent(HttpContext http, UpdateAiConsentRequest request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var service = http.RequestServices.GetRequiredService<IAiConsent>();
        var result = await service.UpdateAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            AiConsentStatus.Updated or AiConsentStatus.Replayed => TypedResults.Ok(result.View),
            AiConsentStatus.Invalid => Error(http, 422, result.Code!),
            AiConsentStatus.Conflict => Error(http, 409, result.Code!),
            AiConsentStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
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
        if (request.AllowedScopes is { Count: > 0 } scopes)
        {
            if (scopes.Distinct().Any(scope => !AiConsentScopes.IsKnown(scope)) ||
                !scopes.Contains("task_drafts"))
                return Error(http, 403, "AI_SCOPE_DENIED");
            var consent = await http.RequestServices.GetRequiredService<IAiConsent>().GetAsync(owner, ct);
            var granted = consent.View?.AllowedScopes ?? [];
            if (!scopes.Distinct().All(granted.Contains)) return Error(http, 403, "AI_SCOPE_DENIED");
        }

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
