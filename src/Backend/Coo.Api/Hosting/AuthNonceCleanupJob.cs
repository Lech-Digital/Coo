using Coo.Api.Application.Auth;

namespace Coo.Api.Hosting;

/// <summary>The daily clean-up of nonce rows older than a day (Spec 010 §4.2). The first run is 15 minutes after start-up.</summary>
internal sealed partial class AuthNonceCleanupJob(PurgeAuthNonces purge, TimeProvider time, ILogger<AuthNonceCleanupJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(15);
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
                    // One failed clean-up must not stop the next one, or the API: a cancellation nobody asked for (a
                    // command timeout) included, which escaping here would stop the host. The type only; no messages.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth nonces: {Count} old rows deleted")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Auth nonce clean-up failed: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
