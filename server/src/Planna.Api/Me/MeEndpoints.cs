using System.Security.Claims;
using Planna.Application.Export;

namespace Planna.Api.Me;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me").WithTags("Identity").RequireAuthorization();
        group.MapPost("/exports", CreateExport).WithName("CreateAccountExport");
        group.MapGet("/exports", ListExports).WithName("ListAccountExports");
        group.MapGet("/exports/{id}", GetExport).WithName("GetAccountExport");
        group.MapGet("/exports/{id}/download", DownloadExport).WithName("DownloadAccountExport");
        return app;
    }

    private static async Task<IResult> CreateExport(HttpContext http, IAccountExportService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"], out var mutation) || mutation == Guid.Empty)
            return Error(http, 400, "INVALID_IDEMPOTENCY_KEY");
        var result = await service.CreateAsync(owner, mutation, ct);
        return result.Status switch
        {
            AccountExportStatus.Created or AccountExportStatus.Replayed =>
                TypedResults.Json(result.Job, statusCode: StatusCodes.Status202Accepted),
            AccountExportStatus.Conflict => Error(http, 409, result.Code!),
            AccountExportStatus.AccountDeleted => Error(http, 401, result.Code!),
            _ => Error(http, 500, "INTERNAL_ERROR")
        };
    }

    private static async Task<IResult> ListExports(HttpContext http, IAccountExportService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await service.ListAsync(owner, ct);
        return result.Status == AccountExportStatus.Listed
            ? TypedResults.Ok(result.Jobs)
            : Error(http, 500, "INTERNAL_ERROR");
    }

    private static async Task<IResult> GetExport(HttpContext http, string id, IAccountExportService service,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var exportId) || exportId == Guid.Empty)
            return Error(http, 400, "INVALID_EXPORT_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await service.GetAsync(owner, exportId, ct);
        return result.Status == AccountExportStatus.Ok
            ? TypedResults.Ok(result.Job)
            : Error(http, 404, result.Code!);
    }

    private static async Task<IResult> DownloadExport(HttpContext http, string id,
        IAccountExportService service, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var exportId) || exportId == Guid.Empty)
            return Error(http, 400, "INVALID_EXPORT_ID");
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var owner) || owner == Guid.Empty)
            return Error(http, 401, "UNAUTHORIZED");
        var result = await service.DownloadAsync(owner, exportId, ct);
        if (result.Status != AccountExportStatus.Ok) return result.Status switch
        {
            AccountExportStatus.NotFound => Error(http, 404, result.Code!),
            AccountExportStatus.Expired => Error(http, 410, result.Code!),
            _ => Error(http, 409, result.Code!)
        };
        http.Response.Headers.ContentDisposition = "attachment; filename=\"export-" + exportId + ".json\"";
        return TypedResults.Text(result.Payload, "application/json");
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
