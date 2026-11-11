using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Planna.Api.Http;

// SEC-01A: baseline transport/interaction hardening kept independent from business endpoints.
public static class SecurityPipeline
{
    public const long MaxJsonBodyBytes = 1_048_576;

    public static IServiceCollection AddApiSecurity(this IServiceCollection services, int aiRateLimitPerMinute)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("ai", http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue("sub")
                ?? http.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = aiRateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    request_id = context.HttpContext.TraceIdentifier,
                    error = new { code = "RATE_LIMITED", message = "RATE_LIMITED", retryable = true }
                }, ct);
            };
        });
        return services;
    }

    public static WebApplication UseApiSecurity(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client disconnect; nothing to write.
            }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Planna.Api.Security")
                    .LogError(ex, "UNHANDLED_REQUEST method={Method} path={Path}",
                        context.Request.Method, context.Request.Path.Value);
                if (context.Response.HasStarted) throw;
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    request_id = context.TraceIdentifier,
                    error = new
                    {
                        code = "INTERNAL_ERROR",
                        message = "INTERNAL_ERROR",
                        retryable = true
                    }
                });
            }
        });

        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength > MaxJsonBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    request_id = context.TraceIdentifier,
                    error = new
                    {
                        code = "PAYLOAD_TOO_LARGE",
                        message = "PAYLOAD_TOO_LARGE",
                        retryable = false
                    }
                });
                return;
            }
            await next();
        });

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                if (!headers.ContainsKey("Cache-Control")) headers["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });
        return app;
    }

    public static bool IsJsonContentType(HttpContext context) =>
        context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
}
