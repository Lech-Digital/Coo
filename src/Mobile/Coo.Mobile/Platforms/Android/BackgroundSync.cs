using Android.App;
using Android.App.Job;
using Android.Content;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Notifications;

namespace Coo.Mobile;

/// <summary>
/// Sync while Coo is closed (ADR 0002 §7): every 15 minutes or so, when there is a network, Android starts Coo's process
/// for one cycle, then plans the reminders and redraws the widget, so a partner's feed moves this phone's feeding reminder
/// and the widget shows it. Android's own JobScheduler, which WorkManager wraps: no library pinned against MAUI's AndroidX.
/// Persisted, so it survives a reboot. Android decides the exact moment and batches it with other apps in Doze.
/// </summary>
[Service(Name = "com.lechdigital.coo.BackgroundSyncJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = true)]
public sealed class BackgroundSyncJob : JobService
{
    private const int JobId = 4_1100;
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Flex = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _running;

    /// <summary>Asks Android for the periodic job once; already scheduled, it is left alone so its clock is not reset.</summary>
    public static void Schedule(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (context.GetSystemService(JobSchedulerService) is not JobScheduler scheduler || scheduler.GetPendingJob(JobId) is not null)
                return;
            var job = new JobInfo.Builder(JobId, new ComponentName(context, Java.Lang.Class.FromType(typeof(BackgroundSyncJob))))
                .SetRequiredNetworkType(NetworkType.Any)!
                .SetPeriodic((long)Every.TotalMilliseconds, (long)Flex.TotalMilliseconds)!
                .SetPersisted(true)!
                .Build();
            scheduler.Schedule(job!);
        }
#pragma warning disable CA1031 // Background sync is a convenience: the app syncs whenever it is open.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Background sync not scheduled: {e.GetType().Name}");
        }
    }

    public override bool OnStartJob(JobParameters? @params)
    {
        _running = new CancellationTokenSource(Longest);
        _ = RunAsync(@params, _running.Token);
        return true;
    }

    // Android wants the time back (the network went, or Doze): the cycle stops, and the job runs again next period.
    public override bool OnStopJob(JobParameters? @params)
    {
        _running?.Cancel();
        return false;
    }

    private async Task RunAsync(JobParameters? @params, CancellationToken cancellationToken)
    {
        try
        {
            if (IPlatformApplication.Current?.Services is not { } services)
                return;
            await services.GetRequiredService<SyncEngine>().SyncNowAsync(cancellationToken);
            await services.GetRequiredService<ReminderRefresher>().RefreshAsync(cancellationToken);
        }
#pragma warning disable CA1031 // A failed cycle is retried at the next period, or when the app opens.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Background sync failed: {e.GetType().Name}");
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            JobFinished(@params, false);
        }
    }
}
