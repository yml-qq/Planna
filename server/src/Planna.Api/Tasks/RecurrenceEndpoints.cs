using System.Globalization;
using System.Security.Claims;
using Planna.Application.Tasks;

namespace Planna.Api.Tasks;

public static class RecurrenceEndpoints
{
    public static IEndpointRouteBuilder MapRecurrenceEndpoints(this IEndpointRouteBuilder app)
    {
        var recurrence = app.MapGroup("/tasks/{taskId}/recurrence").WithTags("Tasks").RequireAuthorization();
        recurrence.MapGet("", GetRecurrence).WithName("GetRecurrence");
        recurrence.MapPut("", PutRecurrence).WithName("PutRecurrence");
        recurrence.MapDelete("", DeleteRecurrence).WithName("DeleteRecurrence");
        var occurrences = app.MapGroup("/tasks/{taskId}/occurrences").WithTags("Tasks").RequireAuthorization();
        occurrences.MapGet("", ListOccurrences).WithName("ListOccurrences");
        return app;
    }

    private static Task<IResult> GetRecurrence(HttpContext http, string taskId, IRecurrenceQuery query, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? GetRecurrenceCore(http, id, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> GetRecurrenceCore(HttpContext http, Guid taskId,
        IRecurrenceQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var (status, recurrence) = await query.GetAsync(owner, taskId, ct);
        return status switch
        {
            GetOccurrenceListStatus.Ok => TypedResults.Ok(recurrence),
            GetOccurrenceListStatus.TaskNotFound => Error(http, 404, "TASK_NOT_FOUND"),
            _ => Error(http, 404, "RECURRENCE_NOT_FOUND")
        };
    }

    private static Task<IResult> PutRecurrence(HttpContext http, string taskId, PutRecurrenceRequest request,
        IPutRecurrence command, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? PutRecurrenceCore(http, id, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> PutRecurrenceCore(HttpContext http, Guid taskId,
        PutRecurrenceRequest request, IPutRecurrence command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, taskId, request, ct);
        return result.Status switch
        {
            PutRecurrenceStatus.Saved or PutRecurrenceStatus.Replayed => TypedResults.Ok(result.Recurrence),
            PutRecurrenceStatus.Invalid => Error(http, 422, result.Code!),
            PutRecurrenceStatus.Unsupported => Error(http, 422, result.Code!),
            PutRecurrenceStatus.TaskNotFound => Error(http, 404, result.Code!),
            PutRecurrenceStatus.NotFound => Error(http, 404, result.Code!),
            PutRecurrenceStatus.Exists => Error(http, 409, result.Code!),
            PutRecurrenceStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            PutRecurrenceStatus.Conflict => Error(http, 409, result.Code!),
            PutRecurrenceStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> DeleteRecurrence(HttpContext http, string taskId,
        IDeleteRecurrence command, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? DeleteRecurrenceCore(http, id, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> DeleteRecurrenceCore(HttpContext http, Guid taskId,
        IDeleteRecurrence command, CancellationToken ct)
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
            DeleteRecurrenceStatus.Deleted or DeleteRecurrenceStatus.Replayed => TypedResults.NoContent(),
            DeleteRecurrenceStatus.TaskNotFound => Error(http, 404, result.Code!),
            DeleteRecurrenceStatus.NotFound => Error(http, 404, result.Code!),
            DeleteRecurrenceStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            DeleteRecurrenceStatus.Conflict => Error(http, 409, result.Code!),
            DeleteRecurrenceStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> ListOccurrences(HttpContext http, string taskId,
        IRecurrenceQuery query, CancellationToken ct) =>
        Guid.TryParse(taskId, out var id) && id != Guid.Empty
            ? ListOccurrencesCore(http, id, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TASK_ID"));

    private static async Task<IResult> ListOccurrencesCore(HttpContext http, Guid taskId,
        IRecurrenceQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today;
        var to = today.AddDays(30);
        if (http.Request.Query.TryGetValue("from", out var rawFrom) &&
            (rawFrom.Count != 1 || !TryDate(rawFrom[0], out from)))
            return Error(http, 400, "INVALID_WINDOW");
        if (http.Request.Query.TryGetValue("to", out var rawTo) &&
            (rawTo.Count != 1 || !TryDate(rawTo[0], out to)))
            return Error(http, 400, "INVALID_WINDOW");
        if (to < from || to.DayNumber - from.DayNumber > 90)
            return Error(http, 400, "INVALID_WINDOW");

        var result = await query.ListOccurrencesAsync(owner, taskId, from, to, ct);
        return result.Status switch
        {
            GetOccurrenceListStatus.Ok => TypedResults.Ok(new OccurrenceListView(result.Items!)),
            GetOccurrenceListStatus.TaskNotFound => Error(http, 404, "TASK_NOT_FOUND"),
            GetOccurrenceListStatus.RecurrenceNotFound => Error(http, 404, "RECURRENCE_NOT_FOUND"),
            _ => Error(http, 400, "INVALID_WINDOW")
        };
    }

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

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
