using System.Globalization;
using System.Security.Claims;
using Planna.Application.Tags;

namespace Planna.Api.Tags;

public static class TagEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tags").WithTags("Tags").RequireAuthorization();
        group.MapGet("", ListTags).WithName("ListTags");
        group.MapPost("", CreateTag).WithName("CreateTag");
        group.MapGet("/{id}", GetTag).WithName("GetTag");
        group.MapPatch("/{id}", PatchTag).WithName("PatchTag");
        group.MapDelete("/{id}", DeleteTag).WithName("DeleteTag");
        return app;
    }

    private static async Task<IResult> ListTags(HttpContext http, ITagReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        return TypedResults.Ok(new TagListView(await query.ListAsync(owner, ct)));
    }

    private static async Task<IResult> CreateTag(HttpContext http, CreateTagRequest request,
        ICreateTag command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            CreateTagStatus.Created or CreateTagStatus.Replayed =>
                TypedResults.Created($"/tags/{result.Tag!.Id}", result.Tag),
            CreateTagStatus.Invalid => Error(http, 422, result.Code!),
            CreateTagStatus.NameConflict => Error(http, 409, result.Code!),
            CreateTagStatus.Conflict => Error(http, 409, result.Code!),
            CreateTagStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> GetTag(HttpContext http, string id, ITagReadQuery query, CancellationToken ct) =>
        Guid.TryParse(id, out var tagId) && tagId != Guid.Empty
            ? GetTagCore(http, tagId, query, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TAG_ID"));

    private static async Task<IResult> GetTagCore(HttpContext http, Guid tagId,
        ITagReadQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await query.GetAsync(owner, tagId, ct);
        return result.Status == GetTagStatus.Ok
            ? TypedResults.Ok(result.Tag)
            : Error(http, 404, "TAG_NOT_FOUND");
    }

    private static Task<IResult> PatchTag(HttpContext http, string id, UpdateTagRequest request,
        IUpdateTag command, CancellationToken ct) =>
        Guid.TryParse(id, out var tagId) && tagId != Guid.Empty
            ? PatchTagCore(http, tagId, request, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TAG_ID"));

    private static async Task<IResult> PatchTagCore(HttpContext http, Guid tagId,
        UpdateTagRequest request, IUpdateTag command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, tagId, request, ct);
        return result.Status switch
        {
            UpdateTagStatus.Updated or UpdateTagStatus.Replayed => TypedResults.Ok(result.Tag),
            UpdateTagStatus.NotFound => Error(http, 404, result.Code!),
            UpdateTagStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            UpdateTagStatus.NameConflict => Error(http, 409, result.Code!),
            UpdateTagStatus.Invalid => Error(http, 422, result.Code!),
            UpdateTagStatus.Conflict => Error(http, 409, result.Code!),
            UpdateTagStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static Task<IResult> DeleteTag(HttpContext http, string id, IDeleteTag command, CancellationToken ct) =>
        Guid.TryParse(id, out var tagId) && tagId != Guid.Empty
            ? DeleteTagCore(http, tagId, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_TAG_ID"));

    private static async Task<IResult> DeleteTagCore(HttpContext http, Guid tagId,
        IDeleteTag command, CancellationToken ct)
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

        var result = await command.ExecuteAsync(owner, mutation, tagId, expectedVersion, ct);
        return result.Status switch
        {
            DeleteTagStatus.Deleted or DeleteTagStatus.Replayed => TypedResults.NoContent(),
            DeleteTagStatus.NotFound => Error(http, 404, result.Code!),
            DeleteTagStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            DeleteTagStatus.Conflict => Error(http, 409, result.Code!),
            DeleteTagStatus.AccountDeleted => Error(http, 401, result.Code!),
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
