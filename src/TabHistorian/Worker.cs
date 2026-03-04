using TabHistorian.Services;

namespace TabHistorian;

public class Worker(SnapshotService snapshotService, IHostApplicationLifetime lifetime, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("TabHistorian taking snapshot...");
            snapshotService.TakeSnapshot();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snapshot failed");
        }

        lifetime.StopApplication();
        return Task.CompletedTask;
    }
}
