using Coo.Api.Application.Billing;

namespace Coo.Api.Hosting;

/// <summary>
/// The hourly recheck of purchases with Google (ADR 0004 §6), the safety net under the Pub/Sub notifications. The first run
/// is 25 minutes after start-up, apart from the other jobs.
/// </summary>
internal sealed partial class PurchaseRecheckJob(
    RecheckPurchases recheck, IConfiguration configuration, TimeProvider time, ILogger<PurchaseRecheckJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without the notification token, Pub/Sub cannot reach the server: purchases change only on this recheck.
        if (string.IsNullOrWhiteSpace(configuration[StartupChecks.RtdnTokenKey]))
            LogNoNotifications(logger, StartupChecks.RtdnTokenKey);

        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (done, failed) = await recheck.ExecuteAsync(stoppingToken);
                    if (done + failed > 0)
                        LogRechecked(logger, done, failed);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failed run must not stop the next one, or the API. Type only.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Purchases: {Checked} read again from Google, {Failed} could not be")]
    private static partial void LogRechecked(ILogger logger, int @checked, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Key} is not set: Play's purchase notifications are off; the hourly recheck keeps purchases current")]
    private static partial void LogNoNotifications(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purchase recheck failed: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
