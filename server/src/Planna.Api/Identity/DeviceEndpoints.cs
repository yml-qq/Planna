using System.Globalization;
using System.Security.Claims;
using Planna.Application.Identity;

namespace Planna.Api.Identity;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Identity").RequireAuthorization();
        group.MapGet("/devices", List).WithName("ListDevices");
        group.MapPost("/devices/{id}/revoke", Revoke).WithName("RevokeDevice");
        group.MapPost("/logout", Logout).WithName("Logout");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, IDeviceSessionQuery query, CancellationToken ct)
    {
        if (!Claims(http, out var owner, out var current)) return Error(http, 401, "UNAUTHORIZED");
        var limit = 50;
        if (http.Request.Query.TryGetValue("limit", out var rawLimit) &&
            (rawLimit.Count != 1 || !int.TryParse(rawLimit[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200))
            return Error(http, 400, "INVALID_LIMIT");

        DevicePageCursor? cursor = null;
        if (http.Request.Query.TryGetValue("cursor", out var rawCursor) &&
            (rawCursor.Count != 1 || !DevicePageCursor.TryDecode(rawCursor[0], out cursor)))
            return Error(http, 400, "INVALID_CURSOR");
        return TypedResults.Ok(await query.ListAsync(owner, current, cursor, limit, ct));
    }

    private static Task<IResult> Revoke(HttpContext http, string id,
        IRevokeDeviceSession command, CancellationToken ct) =>
        Guid.TryParse(id, out var target) && target != Guid.Empty
            ? RevokeCore(http, target, command, ct)
            : Task.FromResult(Error(http, 400, "INVALID_DEVICE_ID"));

    private static Task<IResult> Logout(HttpContext http,
        IRevokeDeviceSession command, CancellationToken ct) =>
        Claims(http, out _, out var current)
            ? RevokeCore(http, current, command, ct)
            : Task.FromResult(Error(http, 401, "UNAUTHORIZED"));

    private static async Task<IResult> RevokeCore(HttpContext http, Guid target,
        IRevokeDeviceSession command, CancellationToken ct)
    {
        if (!Claims(http, out var owner, out _)) return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var requestKey) ||
            requestKey == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var etag = http.Request.Headers.IfMatch.ToString();
        if (etag.Length < 3 || etag[0] != '"' || etag[^1] != '"' ||
            !int.TryParse(etag.AsSpan(1, etag.Length - 2), NumberStyles.None,
                CultureInfo.InvariantCulture, out var expectedVersion) || expectedVersion < 1)
            return Error(http, 400, "INVALID_VERSION");

        var result = await command.ExecuteAsync(owner, target, expectedVersion, requestKey, ct);
        return result switch
        {
            RevokeDeviceStatus.Revoked or RevokeDeviceStatus.Replayed => TypedResults.NoContent(),
            RevokeDeviceStatus.NotFound => Error(http, 404, "DEVICE_NOT_FOUND"),
            _ => Error(http, 409, "VERSION_CONFLICT")
        };
    }

    private static bool Claims(HttpContext http, out Guid owner, out Guid session)
    {
        var hasOwner = Guid.TryParse(http.User.FindFirstValue("sub"), out owner);
        var hasSession = Guid.TryParse(http.User.FindFirstValue("sid"), out session);
        return hasOwner && owner != Guid.Empty && hasSession && session != Guid.Empty;
    }

    private static IResult Error(HttpContext http, int status, string code) =>
        TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = false }
        }, statusCode: status);
}
