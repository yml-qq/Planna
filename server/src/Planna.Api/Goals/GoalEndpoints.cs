using System.Globalization;
using System.Security.Claims;
using Planna.Application.Goals;

namespace Planna.Api.Goals;

public static class GoalEndpoints
{
    public static IEndpointRouteBuilder MapGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/goals").WithTags("Goals").RequireAuthorization();
        group.MapGet("/", List).WithName("ListGoals");
        group.MapPost("/", Create).WithName("CreateGoal");
        group.MapGet("/{id}", Get).WithName("GetGoal");
        group.MapPatch("/{id}", Update).WithName("UpdateGoal");
        group.MapDelete("/{id}", Delete).WithName("DeleteGoal");
        group.MapPost("/{id}/milestones", CreateMilestone).WithName("CreateMilestone");
        group.MapPatch("/{id}/milestones/{milestoneId}", UpdateMilestone).WithName("UpdateMilestone");
        group.MapDelete("/{id}/milestones/{milestoneId}", DeleteMilestone).WithName("DeleteMilestone");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, IGoalService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await service.ListAsync(owner, ct);
        return result.Status == GoalStatus.Ok
            ? TypedResults.Ok(result.Goals)
            : Error(http, 500, "INTERNAL_ERROR");
    }

    private static async Task<IResult> Get(HttpContext http, string id, IGoalService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty)
            return Error(http, 400, "INVALID_GOAL_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await service.GetAsync(owner, goalId, ct);
        return result.Status == GoalStatus.Ok
            ? TypedResults.Ok(result.Goal)
            : Error(http, 404, result.Code!);
    }

    private static async Task<IResult> Create(HttpContext http, CreateGoalRequest request,
        IGoalService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            GoalStatus.Created => TypedResults.Created($"/goals/{result.Goal!.Id}", result.Goal),
            GoalStatus.Replayed => TypedResults.Ok(result.Goal),
            GoalStatus.Invalid => Error(http, 422, result.Code!),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            GoalStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Update(HttpContext http, string id, UpdateGoalRequest request,
        IGoalService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty)
            return Error(http, 400, "INVALID_GOAL_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.UpdateAsync(owner, mutation, goalId, request, ct);
        return result.Status switch
        {
            GoalStatus.Updated or GoalStatus.Replayed => TypedResults.Ok(result.Goal),
            GoalStatus.NotFound => Error(http, 404, result.Code!),
            GoalStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            GoalStatus.Invalid => Error(http, 422, result.Code!),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Delete(HttpContext http, string id, IGoalService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty)
            return Error(http, 400, "INVALID_GOAL_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await service.DeleteAsync(owner, mutation, goalId, version, ct);
        return result.Status switch
        {
            GoalStatus.Deleted or GoalStatus.Replayed => TypedResults.NoContent(),
            GoalStatus.NotFound => Error(http, 404, result.Code!),
            GoalStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> CreateMilestone(HttpContext http, string id,
        CreateMilestoneRequest request, IGoalService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty)
            return Error(http, 400, "INVALID_GOAL_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateMilestoneAsync(owner, mutation, goalId, request, ct);
        return result.Status switch
        {
            GoalStatus.Created => TypedResults.Created(
                $"/goals/{goalId}/milestones/{result.Milestone!.Id}", result.Milestone),
            GoalStatus.Replayed => TypedResults.Ok(result.Milestone),
            GoalStatus.NotFound => Error(http, 404, result.Code!),
            GoalStatus.Invalid => Error(http, 422, result.Code!),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> UpdateMilestone(HttpContext http, string id, string milestoneId,
        UpdateMilestoneRequest request, IGoalService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty ||
            !Guid.TryParse(milestoneId, out var parsedMilestoneId) || parsedMilestoneId == Guid.Empty)
            return Error(http, 400, "INVALID_MILESTONE_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.UpdateMilestoneAsync(owner, mutation, goalId, parsedMilestoneId,
            request, ct);
        return result.Status switch
        {
            GoalStatus.Updated or GoalStatus.Replayed => TypedResults.Ok(result.Milestone),
            GoalStatus.NotFound => Error(http, 404, result.Code!),
            GoalStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            GoalStatus.Invalid => Error(http, 422, result.Code!),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> DeleteMilestone(HttpContext http, string id, string milestoneId,
        IGoalService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var goalId) || goalId == Guid.Empty ||
            !Guid.TryParse(milestoneId, out var parsedMilestoneId) || parsedMilestoneId == Guid.Empty)
            return Error(http, 400, "INVALID_MILESTONE_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await service.DeleteMilestoneAsync(owner, mutation, goalId, parsedMilestoneId,
            version, ct);
        return result.Status switch
        {
            GoalStatus.Deleted or GoalStatus.Replayed => TypedResults.NoContent(),
            GoalStatus.NotFound => Error(http, 404, result.Code!),
            GoalStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            GoalStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static bool TryOwner(HttpContext http, out Guid owner) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out owner) && owner != Guid.Empty;

    private static bool TryMutation(HttpContext http, out Guid mutation) =>
        Guid.TryParse(http.Request.Headers["Idempotency-Key"], out mutation) && mutation != Guid.Empty;

    private static bool TryExpectedVersion(HttpContext http, out long version)
    {
        version = 0;
        var raw = http.Request.Headers.IfMatch.ToString().Trim('"');
        return raw.StartsWith('v') &&
               long.TryParse(raw[1..], NumberStyles.None, CultureInfo.InvariantCulture, out version) &&
               version >= 1;
    }

    private static IResult Error(HttpContext http, int status, string code, object? details = null)
    {
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = code,
            ["retryable"] = status is 429 or 503
        };
        if (details is not null) error["details"] = details;
        return TypedResults.Json(new { request_id = http.TraceIdentifier, error }, statusCode: status);
    }
}
