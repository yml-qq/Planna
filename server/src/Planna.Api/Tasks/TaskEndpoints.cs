using System.Security.Claims;
using Planna.Application.Tasks;

namespace Planna.Api.Tasks;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/tasks").WithTags("Tasks").RequireAuthorization()
            .MapPost("", CreateTask).WithName("CreateTask");
        return app;
    }

    private static async Task<IResult> CreateTask(
        HttpContext http, CreateTaskRequest request, ICreateTask command, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return TypedResults.Unauthorized();
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");

        var result = await command.ExecuteAsync(owner, mutation, request, ct);
        return result.Status switch
        {
            CreateTaskStatus.Created => TypedResults.Created($"/tasks/{result.Task!.Id}", result.Task),
            CreateTaskStatus.Replayed => TypedResults.Created($"/tasks/{result.Task!.Id}", result.Task),
            CreateTaskStatus.Invalid => Error(http, 422, result.Code!),
            CreateTaskStatus.ListNotFound => Error(http, 404, result.Code!),
            CreateTaskStatus.Conflict => Error(http, 409, result.Code!),
            CreateTaskStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static IResult Error(HttpContext http, int status, string code) =>
        TypedResults.Json(new
        {
            request_id = http.TraceIdentifier,
            error = new { code, message = code, retryable = false }
        }, statusCode: status);
}
