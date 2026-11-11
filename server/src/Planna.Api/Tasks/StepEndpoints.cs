using System.Globalization;
using System.Security.Claims;
using Planna.Application.Tasks;

namespace Planna.Api.Tasks;

public static class StepEndpoints
{
    public static IEndpointRouteBuilder MapStepEndpoints(this IEndpointRouteBuilder app)
    {
        var taskSteps = app.MapGroup("/tasks/{taskId}/steps").WithTags("Steps").RequireAuthorization();
        taskSteps.MapGet("", ListSteps).WithName("ListSteps");
        taskSteps.MapPost("", CreateStep).WithName("CreateStep");
        var steps = app.MapGroup("/steps").WithTags("Steps").RequireAuthorization();
        steps.MapPatch("/{id}", PatchStep).WithName("PatchStep");
        steps.MapDelete("/{id}", DeleteStep).WithName("DeleteStep");
        return app;
    }

    private static Task<IResult> ListSteps(HttpContext http, string taskId, IStepReadQuery query, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? ListStepsCore(http, id, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> ListStepsCore(HttpContext http, Guid taskId,
        IStepReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await query.ListAsync(owner, taskId, ct);
        return result.Status == GetStepListStatus.Ok
            ? TypedResults.Ok(new StepListView(result.Items!))
            : Error(http, 404, "TASK_NOT_FOUND");
    }

    private static Task<IResult> CreateStep(HttpContext http, string taskId, CreateStepRequest request,
        ICreateStep command, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? CreateStepCore(http, id, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> CreateStepCore(HttpContext http, Guid taskId,
        CreateStepRequest request, ICreateStep command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, taskId, request, ct);
        return result.Status switch
        {
            CreateStepStatus.Created or CreateStepStatus.Replayed =>
                TypedResults.Created($"/steps/{result.Step!.Id}", result.Step),
            CreateStepStatus.Invalid => Error(http, 422, result.Code!),
            CreateStepStatus.TaskNotFound => Error(http, 404, result.Code!),
            CreateStepStatus.Conflict => Error(http, 409, result.Code!),
            CreateStepStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> PatchStep(HttpContext http, string id, UpdateStepRequest request,
        IUpdateStep command, CancellationToken ct) =>
        Guid.TryParse(id, out var stepId) && stepId != Guid.Empty
            ? PatchStepCore(http, stepId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_STEP_ID"));

    private static async Task<IResult> PatchStepCore(HttpContext http, Guid stepId,
        UpdateStepRequest request, IUpdateStep command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, stepId, request, ct);
        return result.Status switch
        {
            UpdateStepStatus.Updated or UpdateStepStatus.Replayed => TypedResults.Ok(result.Step),
            UpdateStepStatus.NotFound => Error(http, 404, result.Code!),
            UpdateStepStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            UpdateStepStatus.Invalid => Error(http, 422, result.Code!),
            UpdateStepStatus.Conflict => Error(http, 409, result.Code!),
            UpdateStepStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> DeleteStep(HttpContext http, string id, IDeleteStep command, CancellationToken ct) =>
        Guid.TryParse(id, out var stepId) && stepId != Guid.Empty
            ? DeleteStepCore(http, stepId, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_STEP_ID"));

    private static async Task<IResult> DeleteStepCore(HttpContext http, Guid stepId,
        IDeleteStep command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var etag = http.Request.Headers.IfMatch.ToString();
        if (etag.Length < 4 || etag[0] != '"' || etag[^1] != '"' || etag[1] != 'v' ||
            !long.TryParse(etag.AsSpan(2, etag.Length - 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out var expectedVersion) || expectedVersion < 1)
            return Error(http, 400, "INVALID_VERSION");

        var result = await command.ExecuteAsync(owner, mutation, stepId, expectedVersion, ct);
        return result.Status switch
        {
            DeleteStepStatus.Deleted or DeleteStepStatus.Replayed => TypedResults.NoContent(),
            DeleteStepStatus.NotFound => Error(http, 404, result.Code!),
            DeleteStepStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            DeleteStepStatus.Conflict => Error(http, 409, result.Code!),
            DeleteStepStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
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
