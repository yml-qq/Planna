using System.Globalization;
using System.Security.Claims;
using Planna.Application.Notes;

namespace Planna.Api.Notes;

public static class NoteEndpoints
{
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/notes").WithTags("Notes").RequireAuthorization();
        group.MapGet("/", List).WithName("ListNotes");
        group.MapPost("/", Create).WithName("CreateNote");
        group.MapGet("/{id}", Get).WithName("GetNote");
        group.MapPatch("/{id}", Update).WithName("UpdateNote");
        group.MapDelete("/{id}", Delete).WithName("DeleteNote");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, INoteService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var limit = 50;
        var rawLimit = http.Request.Query["limit"].ToString();
        if (rawLimit.Length > 0 &&
            (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 100))
            return Error(http, 400, "INVALID_LIMIT");
        Guid? taskId = null;
        var rawTask = http.Request.Query["task_id"].ToString();
        if (rawTask.Length > 0)
        {
            if (!Guid.TryParse(rawTask, out var parsed) || parsed == Guid.Empty)
                return Error(http, 400, "INVALID_TASK_ID");
            taskId = parsed;
        }
        var result = await service.ListAsync(owner, http.Request.Query["query"].ToString(),
            taskId, limit, ct);
        return result.Status switch
        {
            NoteStatus.Ok => TypedResults.Ok(result.Notes),
            NoteStatus.Invalid => Error(http, 400, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Get(HttpContext http, string id, INoteService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var noteId) || noteId == Guid.Empty)
            return Error(http, 400, "INVALID_NOTE_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await service.GetAsync(owner, noteId, ct);
        return result.Status == NoteStatus.Ok
            ? TypedResults.Ok(result.Note)
            : Error(http, 404, result.Code!);
    }

    private static async Task<IResult> Create(HttpContext http, CreateNoteRequest request,
        INoteService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            NoteStatus.Created => TypedResults.Created($"/notes/{result.Note!.Id}", result.Note),
            NoteStatus.Replayed => TypedResults.Ok(result.Note),
            NoteStatus.NotFound => Error(http, 404, result.Code!),
            NoteStatus.Invalid => Error(http, 422, result.Code!),
            NoteStatus.Conflict => Error(http, 409, result.Code!),
            NoteStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Update(HttpContext http, string id,
        UpdateNoteRequest request, INoteService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var noteId) || noteId == Guid.Empty)
            return Error(http, 400, "INVALID_NOTE_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.UpdateAsync(owner, mutation, noteId, request, ct);
        return result.Status switch
        {
            NoteStatus.Updated or NoteStatus.Replayed => TypedResults.Ok(result.Note),
            NoteStatus.NotFound => Error(http, 404, result.Code!),
            NoteStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            NoteStatus.Invalid => Error(http, 422, result.Code!),
            NoteStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Delete(HttpContext http, string id, INoteService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var noteId) || noteId == Guid.Empty)
            return Error(http, 400, "INVALID_NOTE_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await service.DeleteAsync(owner, mutation, noteId, version, ct);
        return result.Status switch
        {
            NoteStatus.Deleted or NoteStatus.Replayed => TypedResults.NoContent(),
            NoteStatus.NotFound => Error(http, 404, result.Code!),
            NoteStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            NoteStatus.Conflict => Error(http, 409, result.Code!),
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
