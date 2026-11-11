using System.Globalization;
using System.Security.Claims;
using Planna.Application.Tasks;

namespace Planna.Api.Lists;

public static class ListEndpoints
{
    public static IEndpointRouteBuilder MapListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists").WithTags("Lists").RequireAuthorization();
        group.MapGet("", ListLists).WithName("ListLists");
        group.MapPost("", CreateList).WithName("CreateList");
        group.MapGet("/{id}", GetList).WithName("GetList");
        group.MapPatch("/{id}", PatchList).WithName("PatchList");
        group.MapDelete("/{id}", DeleteList).WithName("DeleteList");
        return app;
    }

    private static async Task<IResult> ListLists(HttpContext http, ITaskListReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        return TypedResults.Ok(new TaskListsView(await query.ListAsync(owner, ct)));
    }

    private static async Task<IResult> CreateList(HttpContext http, CreateTaskListRequest request,
        ICreateTaskList command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            CreateTaskListStatus.Created or CreateTaskListStatus.Replayed =>
                TypedResults.Created($"/lists/{result.List!.Id}", result.List),
            CreateTaskListStatus.Invalid => Error(http, 422, result.Code!),
            CreateTaskListStatus.Conflict => Error(http, 409, result.Code!),
            CreateTaskListStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> GetList(HttpContext http, string id, ITaskListReadQuery query, CancellationToken ct) =>
        Guid.TryParse(id, out var listId) && listId != Guid.Empty
            ? GetListCore(http, listId, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_LIST_ID"));

    private static async Task<IResult> GetListCore(HttpContext http, Guid listId,
        ITaskListReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await query.GetAsync(owner, listId, ct);
        return result.Status == GetTaskListStatus.Ok
            ? TypedResults.Ok(result.List)
            : Error(http, 404, "LIST_NOT_FOUND");
    }

    private static Task<IResult> PatchList(HttpContext http, string id, UpdateTaskListRequest request,
        IUpdateTaskList command, CancellationToken ct) =>
        Guid.TryParse(id, out var listId) && listId != Guid.Empty
            ? PatchListCore(http, listId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_LIST_ID"));

    private static async Task<IResult> PatchListCore(HttpContext http, Guid listId,
        UpdateTaskListRequest request, IUpdateTaskList command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, listId, request, ct);
        return result.Status switch
        {
            UpdateTaskListStatus.Updated or UpdateTaskListStatus.Replayed => TypedResults.Ok(result.List),
            UpdateTaskListStatus.NotFound => Error(http, 404, result.Code!),
            UpdateTaskListStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            UpdateTaskListStatus.Invalid => Error(http, 422, result.Code!),
            UpdateTaskListStatus.Conflict => Error(http, 409, result.Code!),
            UpdateTaskListStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> DeleteList(HttpContext http, string id, IDeleteTaskList command, CancellationToken ct) =>
        Guid.TryParse(id, out var listId) && listId != Guid.Empty
            ? DeleteListCore(http, listId, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_LIST_ID"));

    private static async Task<IResult> DeleteListCore(HttpContext http, Guid listId,
        IDeleteTaskList command, CancellationToken ct)
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

        var result = await command.ExecuteAsync(owner, mutation, listId, expectedVersion, ct);
        return result.Status switch
        {
            DeleteTaskListStatus.Deleted or DeleteTaskListStatus.Replayed => TypedResults.NoContent(),
            DeleteTaskListStatus.NotFound => Error(http, 404, result.Code!),
            DeleteTaskListStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            DeleteTaskListStatus.NotEmpty => Error(http, 409, result.Code!),
            DeleteTaskListStatus.InboxProtected => Error(http, 422, result.Code!),
            DeleteTaskListStatus.Conflict => Error(http, 409, result.Code!),
            DeleteTaskListStatus.AccountDeleted => Error(http, 401, result.Code!),
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
