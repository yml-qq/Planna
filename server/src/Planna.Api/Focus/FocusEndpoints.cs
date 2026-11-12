using System.Globalization;
using System.Security.Claims;
using Planna.Application.Focus;

namespace Planna.Api.Focus;

public static class FocusEndpoints
{
    public static IEndpointRouteBuilder MapFocusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/focus-sessions").WithTags("Focus").RequireAuthorization();
        group.MapGet("/", List).WithName("ListFocusSessions");
        group.MapPost("/", Create).WithName("CreateFocusSession");
        group.MapGet("/stats", Stats).WithName("GetFocusStats");
        group.MapGet("/{id}", Get).WithName("GetFocusSession");
        group.MapPatch("/{id}", Update).WithName("UpdateFocusSession");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, IFocusSessionService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var raw = http.Request.Query["limit"].ToString();
        var limit = 20;
        if (raw.Length > 0 &&
            (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 100))
            return Error(http, 400, "INVALID_LIMIT");
        var result = await service.ListAsync(owner, limit, ct);
        return result.Status switch
        {
            FocusStatus.Ok => TypedResults.Ok(result.Sessions),
            FocusStatus.Invalid => Error(http, 400, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Create(HttpContext http, CreateFocusSessionRequest request,
        IFocusSessionService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            FocusStatus.Created => TypedResults.Created($"/focus-sessions/{result.Session!.Id}", result.Session),
            FocusStatus.Replayed => TypedResults.Ok(result.Session),
            FocusStatus.NotFound => Error(http, 404, result.Code!),
            FocusStatus.Invalid => Error(http, 422, result.Code!),
            FocusStatus.Conflict => Error(http, 409, result.Code!),
            FocusStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Stats(HttpContext http, IFocusSessionService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(http, "from", out var from) || !TryDate(http, "to", out var to))
            return Error(http, 400, "INVALID_DATE");
        var timeZone = http.Request.Query["time_zone"].ToString();
        if (timeZone.Length == 0) timeZone = "Asia/Shanghai";
        var result = await service.StatsAsync(owner, from, to, timeZone, ct);
        return result.Status switch
        {
            FocusStatus.Ok => TypedResults.Ok(result.Stats),
            FocusStatus.Invalid => Error(http, 422, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static bool TryDate(HttpContext http, string name, out DateOnly value)
    {
        value = default;
        var raw = http.Request.Query[name].ToString();
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);
    }

    private static async Task<IResult> Get(HttpContext http, string id, IFocusSessionService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var sessionId) || sessionId == Guid.Empty)
            return Error(http, 400, "INVALID_FOCUS_SESSION_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await service.GetAsync(owner, sessionId, ct);
        return result.Status switch
        {
            FocusStatus.Ok => TypedResults.Ok(result.Session),
            FocusStatus.NotFound => Error(http, 404, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Update(HttpContext http, string id,
        UpdateFocusSessionRequest request, IFocusSessionService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var sessionId) || sessionId == Guid.Empty)
            return Error(http, 400, "INVALID_FOCUS_SESSION_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await service.UpdateAsync(owner, mutation, sessionId, version, request, ct);
        return result.Status switch
        {
            FocusStatus.Updated or FocusStatus.Replayed => TypedResults.Ok(result.Session),
            FocusStatus.NotFound => Error(http, 404, result.Code!),
            FocusStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            FocusStatus.Invalid => Error(http, 422, result.Code!),
            FocusStatus.Conflict => Error(http, 409, result.Code!),
            FocusStatus.AccountDeleted => Error(http, 401, result.Code!),
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
