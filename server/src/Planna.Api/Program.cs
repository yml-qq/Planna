using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Planna.Application.Tasks;
using Planna.Application.Identity;
using Planna.Application.Tags;
using Planna.Application.Sync;
using Planna.Application.Ai;
using Planna.Application.Export;
using Planna.Application.Privacy;
using Planna.Api.Identity;
using Planna.Api.Lists;
using Planna.Api.Me;
using Planna.Api.Sync;
using Planna.Api.Tags;
using Planna.Api.Tasks;
using Planna.Api.Ai;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;
using Planna.Infrastructure.Sync;
using Planna.Infrastructure.Tasks;
using Planna.Infrastructure.Tags;
using Planna.Infrastructure.Ai;
using Planna.Infrastructure.Export;
using Planna.Infrastructure.Privacy;

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
var codePepper = builder.Configuration["AUTH_CODE_PEPPER"]
    ?? throw new InvalidOperationException("AUTH_CODE_PEPPER is required.");
if (Encoding.UTF8.GetByteCount(codePepper) < 32)
    throw new InvalidOperationException("AUTH_CODE_PEPPER must contain at least 32 bytes.");
var outboxKey = builder.Configuration["AUTH_OUTBOX_KEY"]
    ?? throw new InvalidOperationException("AUTH_OUTBOX_KEY is required.");
var trustedProxies = (builder.Configuration["AUTH_TRUSTED_PROXIES"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => IPAddress.TryParse(value, out var address) ? address :
        throw new InvalidOperationException("AUTH_TRUSTED_PROXIES must contain IP addresses."))
    .ToArray();
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in trustedProxies)
            options.KnownProxies.Add(proxy);
    });
}

builder.Services.AddDbContext<TaskWriteContext>(options => options.UseNpgsql(connection));
builder.Services.AddDbContext<AuthContext>(options => options.UseNpgsql(connection));
builder.Services.AddSingleton(new LoginCodeCipher(outboxKey));
builder.Services.AddScoped<ICreateTask, CreateTaskService>();
builder.Services.AddScoped<ITaskReadQuery>(_ => new TaskReadQuery(connection));
builder.Services.AddScoped<IUpdateTask, TaskUpdateService>();
builder.Services.AddScoped<IDeleteTask, TaskDeleteService>();
builder.Services.AddScoped<ICompleteTask, TaskCompleteService>();
builder.Services.AddScoped<IReopenTask, TaskReopenService>();
builder.Services.AddScoped<ITaskListReadQuery>(_ => new TaskListQuery(connection));
builder.Services.AddScoped<ICreateTaskList, ListCreateService>();
builder.Services.AddScoped<IUpdateTaskList, ListUpdateService>();
builder.Services.AddScoped<IDeleteTaskList, ListDeleteService>();
builder.Services.AddScoped<ITagReadQuery>(_ => new TagQuery(connection));
builder.Services.AddScoped<ICreateTag, TagCreateService>();
builder.Services.AddScoped<IUpdateTag, TagUpdateService>();
builder.Services.AddScoped<IDeleteTag, TagDeleteService>();
builder.Services.AddScoped<ITaskTagQuery>(_ => new TaskTagQuery(connection));
builder.Services.AddScoped<ISetTaskTags, SetTaskTagsService>();
builder.Services.AddScoped<IStepReadQuery>(_ => new StepQuery(connection));
builder.Services.AddScoped<ICreateStep, StepCreateService>();
builder.Services.AddScoped<IUpdateStep, StepUpdateService>();
builder.Services.AddScoped<IDeleteStep, StepDeleteService>();
builder.Services.AddScoped<ISyncPullQuery>(sp => new SyncPullQuery(connection, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ISyncPushCommand, SyncPushService>();
builder.Services.AddScoped<ISyncBootstrapQuery>(_ => new SyncBootstrapQuery(connection));
builder.Services.AddScoped<IPutRecurrence, RecurrenceService>();
builder.Services.AddScoped<IDeleteRecurrence, RecurrenceService>();
builder.Services.AddScoped<IRecurrenceQuery>(_ => new RecurrenceQuery(connection));
if (!string.Equals(builder.Configuration["AI_PROVIDER"], "none", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAiParseProvider, RuleBasedParseProvider>();
    builder.Services.AddSingleton<IAiBreakdownProvider, RuleBasedBreakdownProvider>();
    builder.Services.AddScoped<ICreateBreakdown, BreakdownService>();
    builder.Services.AddScoped<IEditBreakdown, BreakdownService>();
    builder.Services.AddScoped<IApplyBreakdown, BreakdownService>();
    builder.Services.AddSingleton<IPlanScheduler, RuleBasedPlanScheduler>();
    builder.Services.AddScoped<IProposePlan, PlanService>();
    builder.Services.AddScoped<IApplyPlan, PlanService>();
}
builder.Services.AddScoped<IAccountExportService>(sp =>
    new AccountExportService(connection, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IAccountDeletionService>(sp => new AccountDeletionService(
    sp.GetRequiredService<TaskWriteContext>(), sp.GetRequiredService<IStepUpService>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAccessTokenIssuer>(sp => new AccessTokenIssuer(
    issuer, audience, signingKey, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IAuthService>(sp => new AuthService(
    sp.GetRequiredService<AuthContext>(), sp.GetRequiredService<LoginCodeCipher>(),
    sp.GetRequiredService<IAccessTokenIssuer>(), sp.GetRequiredService<TimeProvider>(), codePepper));
builder.Services.AddScoped<IStepUpService>(sp => new StepUpService(
    sp.GetRequiredService<AuthContext>(), sp.GetRequiredService<LoginCodeCipher>(),
    sp.GetRequiredService<TimeProvider>(), codePepper));
builder.Services.AddScoped<IDeviceSessionQuery>(_ => new DeviceSessionQuery(connection));
builder.Services.AddScoped<IRevokeDeviceSession, RevokeDeviceSession>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var owner) ||
                    !Guid.TryParse(context.Principal?.FindFirst("sid")?.Value, out var session))
                {
                    context.Fail("Missing session claims.");
                    return;
                }
                var auth = context.HttpContext.RequestServices.GetRequiredService<AuthContext>();
                var user = await auth.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == owner);
                if (user?.DeletedAt is not null)
                {
                    context.HttpContext.Items["AuthError"] = "ACCOUNT_DELETED";
                    context.Fail("Account deleted.");
                    return;
                }
                if (user is null || !await auth.Sessions.AsNoTracking().AnyAsync(x =>
                    x.Id == session && x.UserId == owner && x.RevokedAt == null))
                    context.Fail("Session revoked or missing.");
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsJsonAsync(new
                {
                    request_id = context.HttpContext.TraceIdentifier,
                    error = new
                    {
                        code = context.HttpContext.Items["AuthError"] as string ?? "UNAUTHORIZED",
                        message = context.HttpContext.Items["AuthError"] as string ?? "UNAUTHORIZED",
                        retryable = false
                    }
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
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "ok" }));
app.MapTaskEndpoints();
app.MapAuthEndpoints();
app.MapStepUpEndpoints();
app.MapDeviceEndpoints();
app.MapListEndpoints();
app.MapTagEndpoints();
app.MapStepEndpoints();
app.MapSyncEndpoints();
app.MapRecurrenceEndpoints();
app.MapAiEndpoints();
app.MapMeEndpoints();
app.Run();

public partial class Program;
