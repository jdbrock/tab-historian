using TabHistorian.Services;

namespace TabHistorian;

public class Worker(SnapshotService snapshotService, StorageService storage, TabMachineService tabMachine, TabMachineDb tabMachineDb, IHostApplicationLifetime lifetime, ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var overallSw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Read Chrome state (always)
        logger.LogInformation("Step 1: Reading Chrome state...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = ReadChromeState();
        if (state == null)
        {
            logger.LogInformation("No Chrome data to process, shutting down");
            lifetime.StopApplication();
            return;
        }

        var (windows, timestamp) = state.Value;
        int totalTabs = windows.Sum(w => w.Tabs.Count);
        logger.LogInformation("Step 1 complete: {Windows} windows, {Tabs} tabs read in {Elapsed}ms",
            windows.Count, totalTabs, sw.ElapsedMilliseconds);

        // 2. Determine if full snapshot is due
        var lastSnapshot = snapshotService.GetLatestSnapshotTimestamp();
        bool shouldSnapshot = lastSnapshot == null || (timestamp - lastSnapshot.Value) >= SnapshotInterval;
        logger.LogInformation("Full snapshot decision: {Should} (last: {Last}, interval: {Interval} min)",
            shouldSnapshot ? "YES" : "NO",
            lastSnapshot?.ToString("HH:mm:ss") ?? "never",
            SnapshotInterval.TotalMinutes);

        // 3. Backup (only if we're about to take a full snapshot)
        if (shouldSnapshot)
        {
            try
            {
                storage.BackupDatabase();
                tabMachineDb.BackupDatabase();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup failed");
            }
        }

        // 4. Save full snapshot (conditional)
        if (shouldSnapshot)
        {
            try
            {
                snapshotService.SaveSnapshot(windows, timestamp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Snapshot save failed");
            }
        }
        else
        {
            logger.LogInformation("Skipping full snapshot (last was {Ago:F0} min ago)",
                (timestamp - lastSnapshot!.Value).TotalMinutes);
        }

        // 5. Tab Machine (always)
        logger.LogInformation("Step 5: Running Tab Machine...");
        sw.Restart();
        try
        {
            tabMachine.ProcessSnapshot(windows, timestamp);
            logger.LogInformation("Step 5 complete: Tab Machine finished in {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tab Machine processing failed");
        }

        // 6. Prune (only after full snapshot)
        if (shouldSnapshot)
        {
            try
            {
                storage.PruneSnapshots();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pruning failed");
            }
        }

        logger.LogInformation("All tasks complete in {Elapsed}ms, shutting down", overallSw.ElapsedMilliseconds);
        lifetime.StopApplication();
    }

    private (List<Models.ChromeWindow> Windows, DateTime Timestamp)? ReadChromeState()
    {
        try
        {
            return snapshotService.ReadCurrentState();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read Chrome state");
            return null;
        }
    }
}
