using System.Globalization;
using System.Security.Claims;
using Planna.Application.Sync;

namespace Planna.Api.Sync;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sync").WithTags("Sync").RequireAuthorization();
        group.MapGet("/pull", Pull).WithName("PullChanges");
        group.MapPost("/push", Push).WithName("PushMutations");
        return app;
    }

    private static async Task<IResult> Push(HttpContext http, SyncPushRequest request,
        ISyncPushCommand command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (request.DeviceId == Guid.Empty || request.Mutations is null ||
            request.Mutations.Count is < 1 or > 100 ||
            request.Mutations.Any(x => x.MutationId == Guid.Empty || x.EntityId == Guid.Empty))
            return Error(http, 400, "INVALID_SYNC_REQUEST");
        return TypedResults.Ok(await command.ExecuteAsync(owner, request, ct));
    }

    private static async Task<IResult> Pull(HttpContext http, ISyncPullQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var limit = 100;
        if (http.Request.Query.TryGetValue("limit", out var rawLimit) &&
            (rawLimit.Count != 1 || !int.TryParse(rawLimit[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200))
            return Error(http, 400, "INVALID_LIMIT");

        SyncPageCursor? cursor = null;
        if (http.Request.Query.TryGetValue("cursor", out var rawCursor) &&
            (rawCursor.Count != 1 || !SyncPageCursor.TryDecode(rawCursor[0], out cursor)))
            return Error(http, 400, "INVALID_CURSOR");

        var result = await query.PullAsync(owner, cursor, limit, ct);
        return result.Status switch
        {
            SyncPullStatus.Ok => TypedResults.Ok(result.Page),
            SyncPullStatus.CursorExpired => Error(http, 410, "CURSOR_EXPIRED"),
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
