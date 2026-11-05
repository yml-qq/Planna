using Planna.Infrastructure.Identity;

internal sealed class LoginEmailWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<LoginEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextMaintenance = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (clock.GetUtcNow() >= nextMaintenance)
                {
                    var snapshot = await scope.ServiceProvider.GetRequiredService<AuthQueueMaintenance>()
                        .RunOnceAsync(stoppingToken);
                    logger.LogInformation(
                        "AUTH_EMAIL_QUEUE_HEARTBEAT pending={Pending} leased={Leased} failed={Failed} oldest_waiting_seconds={OldestWaitingSeconds} deleted_challenges={DeletedChallenges} deleted_revoked_tokens={DeletedRevokedTokens} deleted_login_failures={DeletedLoginFailures}",
                        snapshot.Pending, snapshot.Leased, snapshot.Failed, snapshot.OldestWaitingSeconds,
                        snapshot.DeletedChallenges, snapshot.DeletedRevokedTokens, snapshot.DeletedLoginFailures);
                    if (snapshot.BacklogAlert)
                        logger.LogError(
                            "AUTH_EMAIL_QUEUE_BACKLOG pending={Pending} leased={Leased} oldest_waiting_seconds={OldestWaitingSeconds}",
                            snapshot.Pending, snapshot.Leased, snapshot.OldestWaitingSeconds);
                    nextMaintenance = clock.GetUtcNow().AddMinutes(1);
                }
                var dispatcher = scope.ServiceProvider.GetRequiredService<LoginEmailDispatcher>();
                if (await dispatcher.DispatchOneAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Login email worker iteration failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
