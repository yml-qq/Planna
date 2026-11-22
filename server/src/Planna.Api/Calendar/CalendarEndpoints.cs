using System.Globalization;
using System.Security.Claims;
using Planna.Application.Calendar;

namespace Planna.Api.Calendar;

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/calendar").WithTags("Calendar").RequireAuthorization();
        group.MapGet("/workday-overrides", List).WithName("ListWorkdayOverrides");
        group.MapGet("/workdays", Evaluate).WithName("EvaluateWorkdays");
        group.MapPost("/workday-overrides/import", Import).WithName("ImportWorkdayOverrides");
        group.MapPut("/workday-overrides/{date}", Upsert).WithName("UpsertWorkdayOverride");
        group.MapDelete("/workday-overrides/{date}", Delete).WithName("DeleteWorkdayOverride");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, IWorkdayOverrideService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(http.Request.Query["from"].ToString(), out var from) ||
            !TryDate(http.Request.Query["to"].ToString(), out var to))
            return Error(http, 400, "INVALID_RANGE");
        var result = await service.ListAsync(owner, http.Request.Query["jurisdiction"], from, to, ct);
        return result.Status switch
        {
            WorkdayOverrideStatus.Ok => TypedResults.Ok(result.List),
            WorkdayOverrideStatus.Invalid => Error(http, 400, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Evaluate(HttpContext http, IWorkdayOverrideService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(http.Request.Query["from"].ToString(), out var from) ||
            !TryDate(http.Request.Query["to"].ToString(), out var to))
            return Error(http, 400, "INVALID_RANGE");
        var result = await service.EvaluateAsync(owner, http.Request.Query["jurisdiction"], from, to, ct);
        return result.Status switch
        {
            WorkdayOverrideStatus.Ok => TypedResults.Ok(result.Days),
            WorkdayOverrideStatus.Invalid => Error(http, 400, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Import(HttpContext http,
        ImportWorkdayOverridesRequest request, IWorkdayOverrideService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await service.ImportAsync(owner, request, ct);
        return result.Status switch
        {
            WorkdayOverrideStatus.Ok => TypedResults.Ok(result.Imported),
            WorkdayOverrideStatus.Invalid => Error(http, 422, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Upsert(HttpContext http, string date,
        UpsertWorkdayOverrideRequest request, IWorkdayOverrideService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(date, out var day)) return Error(http, 400, "INVALID_DATE");
        var result = await service.UpsertAsync(owner, day, request, ct);
        return result.Status switch
        {
            WorkdayOverrideStatus.Created => TypedResults.Created("/calendar/workday-overrides/" +
                result.Override!.Date, result.Override),
            WorkdayOverrideStatus.Updated => TypedResults.Ok(result.Override),
            WorkdayOverrideStatus.Invalid => Error(http, 422, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Delete(HttpContext http, string date,
        IWorkdayOverrideService service, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(date, out var day)) return Error(http, 400, "INVALID_DATE");
        var result = await service.DeleteAsync(owner, day, http.Request.Query["jurisdiction"], ct);
        return result.Status switch
        {
            WorkdayOverrideStatus.Ok => TypedResults.NoContent(),
            WorkdayOverrideStatus.NotFound => Error(http, 404, result.Code!),
            WorkdayOverrideStatus.Invalid => Error(http, 400, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static bool TryDate(string raw, out DateOnly date) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    private static bool TryOwner(HttpContext http, out Guid owner) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out owner) && owner != Guid.Empty;

    private static IResult Error(HttpContext http, int status, string code, object? details = null)
    {
        http.Response.StatusCode = status;
        return TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = false, details }
        }, statusCode: status);
    }
}
