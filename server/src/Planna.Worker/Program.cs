using Microsoft.EntityFrameworkCore;
using Planna.Application.Identity;
using Planna.Infrastructure.Identity;
using Planna.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;
var connection = configuration["PLANNADB_CONNECTION"]
    ?? throw new InvalidOperationException("PLANNADB_CONNECTION is required.");
var outboxKey = configuration["AUTH_OUTBOX_KEY"]
    ?? throw new InvalidOperationException("AUTH_OUTBOX_KEY is required.");
var smtpHost = configuration["SMTP_HOST"]
    ?? throw new InvalidOperationException("SMTP_HOST is required.");
var smtpUser = configuration["SMTP_USER"]
    ?? throw new InvalidOperationException("SMTP_USER is required.");
var smtpPassword = configuration["SMTP_PASSWORD"]
    ?? throw new InvalidOperationException("SMTP_PASSWORD is required.");
var smtpFrom = configuration["SMTP_FROM"]
    ?? throw new InvalidOperationException("SMTP_FROM is required.");
if (!int.TryParse(configuration["SMTP_PORT"], out var smtpPort) || smtpPort is < 1 or > 65535)
    throw new InvalidOperationException("SMTP_PORT must be a valid port.");

builder.Services.AddDbContext<AuthContext>(options => options.UseNpgsql(connection));
builder.Services.AddSingleton(new LoginCodeCipher(outboxKey));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ILoginCodeSender>(_ => new SmtpLoginCodeSender(
    smtpHost, smtpPort, smtpUser, smtpPassword, smtpFrom));
builder.Services.AddScoped<LoginEmailDispatcher>();
builder.Services.AddScoped<AuthQueueMaintenance>();
builder.Services.AddHostedService<LoginEmailWorker>();
await builder.Build().RunAsync();
