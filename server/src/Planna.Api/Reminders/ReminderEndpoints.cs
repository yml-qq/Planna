using System.Globalization;
using System.Security.Claims;
using Planna.Application.Reminders;

namespace Planna.Api.Reminders;

public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder app)
    {
        var taskGroup = app.MapGroup("/tasks/{taskId}/reminders").WithTags("Reminders")
            .RequireAuthorization();
        taskGroup.MapGet("/", List).WithName("ListTaskReminders");
        taskGroup.MapPost("/", Create).WithName("CreateTaskReminder");
        taskGroup.MapPatch("/{reminderId}", Update).WithName("UpdateTaskReminder");
        taskGroup.MapDelete("/{reminderId}", Delete).WithName("DeleteTaskReminder");

        app.MapGet("/reminders/upcoming", Upcoming).WithTags("Reminders")
            .RequireAuthorization().WithName("ListUpcomingReminders");
        return app;
    }

    private static async Task<IResult> List(HttpContext http, string taskId,
        IReminderService service, CancellationToken ct)
    {
        if (!Guid.TryParse(taskId, out var parsedTaskId) || parsedTaskId == Guid.Empty)
            return Error(http, 400, "INVALID_TASK_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var includeDisabled = http.Request.Query["include_disabled"].ToString() is "true" or "1";
        var result = await service.ListAsync(owner, parsedTaskId, includeDisabled, ct);
        return result.Status switch
        {
            ReminderStatus.Ok => TypedResults.Ok(result.Reminders),
            ReminderStatus.NotFound => Error(http, 404, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Upcoming(HttpContext http, IReminderService service,
        CancellationToken ct)
    {
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        var now = DateTimeOffset.UtcNow;
        var from = now;
        if (!TryInstant(http, "from", out from, now)) return Error(http, 400, "INVALID_DATE");
        var to = from.AddDays(7);
        if (!TryInstant(http, "to", out to, from.AddDays(7))) return Error(http, 400, "INVALID_DATE");
        var limit = 100;
        var rawLimit = http.Request.Query["limit"].ToString();
        if (rawLimit.Length > 0 &&
            (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 200))
            return Error(http, 400, "INVALID_LIMIT");
        var result = await service.UpcomingAsync(owner, from, to, limit, ct);
        return result.Status switch
        {
            ReminderStatus.Ok => TypedResults.Ok(result.Reminders),
            ReminderStatus.Invalid => Error(http, 422, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Create(HttpContext http, string taskId,
        CreateReminderRequest request, IReminderService service, CancellationToken ct)
    {
        if (!Guid.TryParse(taskId, out var parsedTaskId) || parsedTaskId == Guid.Empty)
            return Error(http, 400, "INVALID_TASK_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateAsync(owner, mutation, parsedTaskId, request, ct);
        return result.Status switch
        {
            ReminderStatus.Created => TypedResults.Created(
                $"/tasks/{parsedTaskId}/reminders/{result.Reminder!.Id}", result.Reminder),
            ReminderStatus.Replayed => TypedResults.Ok(result.Reminder),
            ReminderStatus.NotFound => Error(http, 404, result.Code!),
            ReminderStatus.Invalid => Error(http, 422, result.Code!),
            ReminderStatus.Conflict => Error(http, 409, result.Code!),
            ReminderStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Update(HttpContext http, string taskId, string reminderId,
        UpdateReminderRequest request, IReminderService service, CancellationToken ct)
    {
        if (!Guid.TryParse(taskId, out var parsedTaskId) || parsedTaskId == Guid.Empty ||
            !Guid.TryParse(reminderId, out var parsedReminderId) || parsedReminderId == Guid.Empty)
            return Error(http, 400, "INVALID_REMINDER_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.UpdateAsync(owner, mutation, parsedTaskId, parsedReminderId,
            request, ct);
        return result.Status switch
        {
            ReminderStatus.Updated or ReminderStatus.Replayed => TypedResults.Ok(result.Reminder),
            ReminderStatus.NotFound => Error(http, 404, result.Code!),
            ReminderStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            ReminderStatus.Invalid => Error(http, 422, result.Code!),
            ReminderStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> Delete(HttpContext http, string taskId, string reminderId,
        IReminderService service, CancellationToken ct)
    {
        if (!Guid.TryParse(taskId, out var parsedTaskId) || parsedTaskId == Guid.Empty ||
            !Guid.TryParse(reminderId, out var parsedReminderId) || parsedReminderId == Guid.Empty)
            return Error(http, 400, "INVALID_REMINDER_ID");
        if (!TryOwner(http, out var owner)) return Error(http, 401, "UNAUTHORIZED");
        if (!TryMutation(http, out var mutation)) return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        if (!TryExpectedVersion(http, out var version)) return Error(http, 400, "INVALID_VERSION");
        var result = await service.DeleteAsync(owner, mutation, parsedTaskId, parsedReminderId,
            version, ct);
        return result.Status switch
        {
            ReminderStatus.Deleted or ReminderStatus.Replayed => TypedResults.NoContent(),
            ReminderStatus.NotFound => Error(http, 404, result.Code!),
            ReminderStatus.VersionConflict => Error(http, 409, "VERSION_CONFLICT", result.Details),
            ReminderStatus.Conflict => Error(http, 409, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static bool TryInstant(HttpContext http, string name, out DateTimeOffset value,
        DateTimeOffset fallback)
    {
        value = fallback;
        var raw = http.Request.Query[name].ToString();
        if (raw.Length == 0) return true;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
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
