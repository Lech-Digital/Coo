using System.Diagnostics.Metrics;
using Coo.Api.Application.CareLog;

namespace Coo.Api.Hosting;

/// <summary>
/// The daily integrity scan (ADR 0010 G5): every row through the codecs and the Domain rules. Failures are logged by
/// row ID and counted in <c>coo.integrity.failures</c>, for an alert. The first run is ten minutes after start-up.
/// </summary>
internal sealed partial class IntegrityScanJob(VerifyIntegrity verify, TimeProvider time, ILogger<IntegrityScanJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly Meter Meter = new("Coo.Api.Integrity");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("coo.integrity.failures");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await verify.ExecuteAsync(cancellationToken);
            foreach (var failure in report.Failures)
                LogFailure(logger, failure.Id.Value, failure.Kind, failure.Reason);
            Failures.Add(report.Failures.Count);
            LogDone(logger, report.Checked, report.Failures.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One failed scan must not stop the next one, or the API.
            LogScanFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Integrity: entry {EntryId} (kind {Kind}) does not decode: {Reason}")]
    private static partial void LogFailure(ILogger logger, Guid entryId, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Integrity scan: {Checked} rows checked, {Failures} failures")]
    private static partial void LogDone(ILogger logger, int @checked, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integrity scan failed")]
    private static partial void LogScanFailed(ILogger logger, Exception exception);
}
