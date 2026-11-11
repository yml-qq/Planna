using Planna.Infrastructure.Privacy;

internal sealed class PrivacyCleanupWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<PrivacyCleanupWorker> logger,
    TimeSpan retention, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AccountCleanupService>();
                var report = await service.RunOnceAsync(clock.GetUtcNow(), retention, stoppingToken);
                if (report.PurgedRequests > 0 || report.FailedRequests > 0)
                    logger.LogInformation(
                        "PRIVACY_CLEANUP_HEARTBEAT purged={Purged} failed={Failed} retention_days={Days}",
                        report.PurgedRequests, report.FailedRequests, retention.TotalDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Privacy cleanup worker iteration failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
