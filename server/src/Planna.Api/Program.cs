using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Planna.Application.Tasks;
using Planna.Api.Tasks;
using Planna.Infrastructure.Persistence;
using Planna.Infrastructure.Tasks;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration["PLANNADB_CONNECTION"]
    ?? throw new InvalidOperationException("PLANNADB_CONNECTION is required.");
var issuer = builder.Configuration["JWT_ISSUER"]
    ?? throw new InvalidOperationException("JWT_ISSUER is required.");
var audience = builder.Configuration["JWT_AUDIENCE"]
    ?? throw new InvalidOperationException("JWT_AUDIENCE is required.");
var signingKey = builder.Configuration["JWT_SIGNING_KEY"]
    ?? throw new InvalidOperationException("JWT_SIGNING_KEY is required.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("JWT_SIGNING_KEY must contain at least 32 bytes.");

builder.Services.AddDbContext<TaskWriteContext>(options => options.UseNpgsql(connection));
builder.Services.AddScoped<ICreateTask, CreateTaskService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsJsonAsync(new
                {
                    request_id = context.HttpContext.TraceIdentifier,
                    error = new { code = "UNAUTHORIZED", message = "UNAUTHORIZED", retryable = false }
                });
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "ok" }));
app.MapTaskEndpoints();
app.Run();

public partial class Program;
