using Coo.Api.Application.Sync;

namespace Coo.Api.Hosting;

/// <summary>
/// The daily purge of change results and conflict records older than 30 days (Spec 011 §7.2). The first run is 20
/// minutes after start-up, apart from the nonce clean-up's 15.
/// </summary>
internal sealed partial class SyncHistoryCleanupJob(PurgeSyncHistory purge, TimeProvider time, ILogger<SyncHistoryCleanupJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await purge.ExecuteAsync(stoppingToken);
                    LogPurged(logger, deleted);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failed purge must not stop the next one, or the API (a command timeout included). Type only.
                    LogFailed(logger, e.GetType().Name);
                }

                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync history: {Count} old rows deleted")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync history clean-up failed: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
