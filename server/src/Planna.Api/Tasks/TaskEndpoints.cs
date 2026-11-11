using System.Globalization;
using System.Security.Claims;
using Planna.Application.Tasks;

namespace Planna.Api.Tasks;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tasks").WithTags("Tasks").RequireAuthorization();
        group.MapPost("", CreateTask).WithName("CreateTask");
        group.MapGet("", ListTasks).WithName("ListTasks");
        group.MapGet("/{id}", GetTask).WithName("GetTask");
        group.MapPatch("/{id}", PatchTask).WithName("PatchTask");
        group.MapDelete("/{id}", DeleteTask).WithName("DeleteTask");
        group.MapPost("/{id}/complete", CompleteTask).WithName("CompleteTask");
        group.MapPost("/{id}/reopen", ReopenTask).WithName("ReopenTask");
        return app;
    }

    private static Task<IResult> CompleteTask(HttpContext http, string id, CompleteTaskRequest request,
        ICompleteTask command, CancellationToken ct) =>
        Guid.TryParse(id, out var taskId) && taskId != Guid.Empty
            ? CompleteTaskCore(http, taskId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> CompleteTaskCore(HttpContext http, Guid taskId,
        CompleteTaskRequest request, ICompleteTask command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, taskId, request, ct);
        return result.Status switch
        {
            CompleteTaskStatus.Completed or CompleteTaskStatus.Replayed => TypedResults.Ok(result.Task),
            CompleteTaskStatus.NotFound => Error(http, 404, result.Code!),
            CompleteTaskStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            CompleteTaskStatus.AlreadyCompleted => Error(http, 409, result.Code!),
            CompleteTaskStatus.InvalidState => Error(http, 422, result.Code!),
            CompleteTaskStatus.RecurringTask => Error(http, 422, result.Code!),
            CompleteTaskStatus.InvalidOccurrence => Error(http, 422, result.Code!),
            CompleteTaskStatus.Invalid => Error(http, 422, result.Code!),
            CompleteTaskStatus.Conflict => Error(http, 409, result.Code!),
            CompleteTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> ReopenTask(HttpContext http, string id, ReopenTaskRequest request,
        IReopenTask command, CancellationToken ct) =>
        Guid.TryParse(id, out var taskId) && taskId != Guid.Empty
            ? ReopenTaskCore(http, taskId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> ReopenTaskCore(HttpContext http, Guid taskId,
        ReopenTaskRequest request, IReopenTask command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, taskId, request, ct);
        return result.Status switch
        {
            ReopenTaskStatus.Reopened or ReopenTaskStatus.Replayed => TypedResults.Ok(result.Task),
            ReopenTaskStatus.NotFound => Error(http, 404, result.Code!),
            ReopenTaskStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            ReopenTaskStatus.NotCompleted => Error(http, 409, result.Code!),
            ReopenTaskStatus.InvalidState => Error(http, 422, result.Code!),
            ReopenTaskStatus.Invalid => Error(http, 422, result.Code!),
            ReopenTaskStatus.Conflict => Error(http, 409, result.Code!),
            ReopenTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> GetTask(HttpContext http, string id, ITaskReadQuery query, CancellationToken ct) =>
        Guid.TryParse(id, out var taskId) && taskId != Guid.Empty
            ? GetTaskCore(http, taskId, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> GetTaskCore(HttpContext http, Guid taskId,
        ITaskReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await query.GetAsync(owner, taskId, ct);
        return result.Status == TaskGetStatus.Ok
            ? TypedResults.Ok(result.Task)
            : Error(http, 404, "TASK_NOT_FOUND");
    }

    private static Task<IResult> PatchTask(HttpContext http, string id, UpdateTaskRequest request,
        IUpdateTask command, CancellationToken ct) =>
        Guid.TryParse(id, out var taskId) && taskId != Guid.Empty
            ? PatchTaskCore(http, taskId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> PatchTaskCore(HttpContext http, Guid taskId,
        UpdateTaskRequest request, IUpdateTask command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, taskId, request, ct);
        return result.Status switch
        {
            UpdateTaskStatus.Updated or UpdateTaskStatus.Replayed => TypedResults.Ok(result.Task),
            UpdateTaskStatus.NotFound => Error(http, 404, result.Code!),
            UpdateTaskStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            UpdateTaskStatus.Invalid => Error(http, 422, result.Code!),
            UpdateTaskStatus.Conflict => Error(http, 409, result.Code!),
            UpdateTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> DeleteTask(HttpContext http, string id, IDeleteTask command, CancellationToken ct) =>
        Guid.TryParse(id, out var taskId) && taskId != Guid.Empty
            ? DeleteTaskCore(http, taskId, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> DeleteTaskCore(HttpContext http, Guid taskId,
        IDeleteTask command, CancellationToken ct)
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

        var result = await command.ExecuteAsync(owner, mutation, taskId, expectedVersion, ct);
        return result.Status switch
        {
            DeleteTaskStatus.Deleted or DeleteTaskStatus.Replayed => TypedResults.NoContent(),
            DeleteTaskStatus.NotFound => Error(http, 404, result.Code!),
            DeleteTaskStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            DeleteTaskStatus.Conflict => Error(http, 409, result.Code!),
            DeleteTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> ListTasks(HttpContext http, ITaskReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");

        var limit = 50;
        if (http.Request.Query.TryGetValue("limit", out var rawLimit) &&
            (rawLimit.Count != 1 || !int.TryParse(rawLimit[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200))
            return Error(http, 400, "INVALID_LIMIT");

        TaskPageCursor? cursor = null;
        if (http.Request.Query.TryGetValue("cursor", out var rawCursor) &&
            (rawCursor.Count != 1 || !TaskPageCursor.TryDecode(rawCursor[0], out cursor)))
            return Error(http, 400, "INVALID_CURSOR");

        Guid? listId = null;
        if (http.Request.Query.TryGetValue("list_id", out var rawList))
        {
            if (rawList.Count != 1 || !Guid.TryParse(rawList[0], out var parsedList) || parsedList == Guid.Empty)
                return Error(http, 400, "INVALID_LIST_ID");
            listId = parsedList;
        }

        string? status = null;
        if (http.Request.Query.TryGetValue("status", out var rawStatus))
        {
            if (rawStatus.Count != 1 || rawStatus[0] is not ("todo" or "in_progress" or "done" or "archived"))
                return Error(http, 400, "INVALID_STATUS");
            status = rawStatus[0];
        }

        var result = await query.ListAsync(owner, cursor, limit, listId, status, ct);
        return result.Status == TaskReadStatus.Ok
            ? TypedResults.Ok(result.Page)
            : Error(http, 404, "LIST_NOT_FOUND");
    }

    private static async Task<IResult> CreateTask(
        HttpContext http, CreateTaskRequest request, ICreateTask command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            CreateTaskStatus.Created => TypedResults.Created($"/tasks/{result.Task!.Id}", result.Task),
            CreateTaskStatus.Replayed => TypedResults.Created($"/tasks/{result.Task!.Id}", result.Task),
            CreateTaskStatus.Invalid => Error(http, 422, result.Code!),
            CreateTaskStatus.ListNotFound => Error(http, 404, result.Code!),
            CreateTaskStatus.Conflict => Error(http, 409, result.Code!),
            CreateTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
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
