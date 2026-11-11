using System.Globalization;
using System.Security.Claims;
using Planna.Application.Habits;

namespace Planna.Api.Habits;

public static class HabitEndpoints
{
    public static IEndpointRouteBuilder MapHabitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/habits").WithTags("Habits").RequireAuthorization();
        group.MapGet("/", List).WithName("ListHabits");
        group.MapPost("/", Create).WithName("CreateHabit");
        group.MapGet("/{id}", Get).WithName("GetHabit");
        group.MapPatch("/{id}", Update).WithName("UpdateHabit");
        group.MapDelete("/{id}", Delete).WithName("DeleteHabit");
        group.MapPut("/{id}/logs/{date}", UpsertLog).WithName("UpsertHabitLog");
        group.MapGet("/{id}/logs", ListLogs).WithName("ListHabitLogs");
        return app;
    }

    private static async Task<IResult> UpsertLog(HttpContext http, string id, string date,
        UpsertHabitLogRequest request, IHabitLogUpsert command, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var habitId) || habitId == Guid.Empty)
            return Error(http, 400, "INVALID_HABIT_ID");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var logDate))
            return Error(http, 400, "INVALID_DATE");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await command.ExecuteAsync(owner, mutation, habitId, logDate, request, ct);
        return result.Status switch
        {
            HabitLogStatus.Upserted or HabitLogStatus.Replayed => TypedResults.Ok(result.Log),
            HabitLogStatus.NotFound => Error(http, 404, result.Code!),
            HabitLogStatus.Invalid => Error(http, 422, result.Code!),
            HabitLogStatus.Conflict => Error(http, 409, result.Code!),
            HabitLogStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> ListLogs(HttpContext http, string id, IHabitLogQuery query,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var habitId) || habitId == Guid.Empty)
            return Error(http, 400, "INVALID_HABIT_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryDate(http, "from", out var from) || !TryDate(http, "to", out var to))
            return Error(http, 400, "INVALID_DATE");
        var result = await query.ListAsync(owner, habitId, from, to, ct);
        return result.Status switch
        {
            HabitLogStatus.Ok => TypedResults.Ok(result.Logs),
            HabitLogStatus.NotFound => Error(http, 404, result.Code!),
            HabitLogStatus.Invalid => Error(http, 422, result.Code!),
            HabitLogStatus.AccountDeleted => Error(http, 401, result.Code!),
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

    private static async Task<IResult> List(HttpContext http, IHabitQuery query, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await query.ListAsync(owner, ct);
        return result.Status == HabitStatus.Ok ? TypedResults.Ok(result.Habits) : Error(http, 500, "INTERNAL_ERROR");
    }

    private static async Task<IResult> Get(HttpContext http, string id, IHabitQuery query, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var habitId) || habitId == Guid.Empty)
            return Error(http, 400, "INVALID_HABIT_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var result = await query.GetAsync(owner, habitId, ct);
        return result.Status == HabitStatus.Ok
            ? TypedResults.Ok(result.Habit)
            : Error(http, 404, result.Code!);
    }

    private static async Task<IResult> Create(HttpContext http, CreateHabitRequest request,
        IHabitCreate command, CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await command.ExecuteAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            HabitStatus.Created or HabitStatus.Replayed =>
                TypedResults.Created($"/habits/{result.Habit!.Id}", result.Habit),
            HabitStatus.Invalid => Error(http, 422, result.Code!),
            HabitStatus.AccountDeleted => Error(http, 401, result.Code!),
            HabitStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Update(HttpContext http, string id, UpdateHabitRequest request,
        IHabitUpdate command, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var habitId) || habitId == Guid.Empty)
            return Error(http, 400, "INVALID_HABIT_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await command.ExecuteAsync(owner, mutation, habitId, request, ct);
        return result.Status switch
        {
            HabitStatus.Updated or HabitStatus.Replayed => TypedResults.Ok(result.Habit),
            HabitStatus.NotFound => Error(http, 404, result.Code!),
            HabitStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            HabitStatus.Invalid => Error(http, 422, result.Code!),
            HabitStatus.Conflict => Error(http, 409, result.Code!),
            HabitStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Delete(HttpContext http, string id, IHabitDelete command,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var habitId) || habitId == Guid.Empty)
            return Error(http, 400, "INVALID_HABIT_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await command.ExecuteAsync(owner, mutation, habitId, version, ct);
        return result.Status switch
        {
            HabitStatus.Deleted or HabitStatus.Replayed => TypedResults.NoContent(),
            HabitStatus.NotFound => Error(http, 404, result.Code!),
            HabitStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            HabitStatus.Conflict => Error(http, 409, result.Code!),
            HabitStatus.AccountDeleted => Error(http, 401, result.Code!),
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
