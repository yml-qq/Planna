using Planna.Application.Notes;

namespace Planna.Worker;

internal sealed class NoteAttachmentCleanupWorker(
    INoteAttachmentCleanup cleanup, TimeProvider clock, ILogger<NoteAttachmentCleanupWorker> logger,
    TimeSpan retention, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await cleanup.PurgeDeletedAsync(clock.GetUtcNow() - retention,
                    stoppingToken);
                if (report.PurgedAttachments > 0)
                    logger.LogInformation(
                        "NOTE_ATTACHMENT_CLEANUP heartbeat purged={Purged} files={Files} retention_days={Days}",
                        report.PurgedAttachments, report.RemovedFiles, retention.TotalDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Note attachment cleanup iteration failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
