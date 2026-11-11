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
        return app;
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

    private static IResult Error(HttpContext http, int status, string code) =>
        TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = false }
        }, statusCode: status);
}
