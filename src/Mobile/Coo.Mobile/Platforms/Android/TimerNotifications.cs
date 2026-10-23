using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Notifications;

namespace Coo.Mobile;

/// <summary>
/// A running feed, sleep or pump in the notification bar: its clock counts by itself, and its buttons switch side,
/// pause, stop or end the sleep from the lock screen, one-handed, without opening Coo. Silent and ongoing; gone when
/// nothing runs. Without the notification permission (asked when a reminder or the first timer starts) it simply
/// does not show.
/// </summary>
public sealed class TimerNotifications : ITimerNotifications
{
    private const string ChannelId = "timers";
    private const int FeedId = 7_001;
    private const int SleepId = 7_002;
    private const int PumpId = 7_003;

    public void Show(IReadOnlyList<RunningTimer> running, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(labels);
        var context = global::Android.App.Application.Context;
        var manager = NotificationManagerCompat.From(context);
        if (manager is null)
            return;
        string L(string key, string fallback) => labels.TryGetValue(key, out var text) ? text : fallback;
        EnsureChannel(context, L(WidgetLabels.Channel, "Running timers"));

        var shown = new HashSet<int>();
        foreach (var timer in running)
        {
            var (id, actions) = timer.Kind switch
            {
                RunningKind.FeedLeft or RunningKind.FeedRight => (FeedId, new[]
                {
                    (QuickAction.SwitchSide, L(WidgetLabels.Switch, "Switch")),
                    (QuickAction.PauseFeed, L(WidgetLabels.Pause, "Pause")),
                    (QuickAction.StopFeed, L(WidgetLabels.Stop, "Stop")),
                }),
                RunningKind.FeedPaused => (FeedId, new[]
                {
                    (QuickAction.FeedLeft, L(WidgetLabels.FeedLeft, "Left")),
                    (QuickAction.FeedRight, L(WidgetLabels.FeedRight, "Right")),
                    (QuickAction.StopFeed, L(WidgetLabels.Stop, "Stop")),
                }),
                RunningKind.Sleep => (SleepId, new[] { (QuickAction.WokeUp, L(WidgetLabels.WokeUp, "Woke up")) }),
                _ => (PumpId, new[] { (QuickAction.StopPump, L(WidgetLabels.StopPump, "Stop pump")) }),
            };
            shown.Add(id);
            try
            {
                manager.Notify(id, Build(context, timer, actions, L(WidgetLabels.TimerHidden, "Timer running")));
            }
            catch (Java.Lang.SecurityException)
            {
                // No notification permission: the timer runs in the app as before.
            }
        }

        foreach (var id in new[] { FeedId, SleepId, PumpId }.Where(i => !shown.Contains(i)))
            manager.Cancel(id);
    }

    private static Notification Build(Context context, RunningTimer timer, (QuickAction Action, string Text)[] actions, string hiddenText)
    {
        var icon = context.Resources?.GetIdentifier("ic_stat_coo", "drawable", context.PackageName) is > 0 and var found ? found : context.ApplicationInfo?.Icon ?? 0;
        var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
        var tap = open is null ? null : PendingIntent.GetActivity(context, 7_000, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var paused = timer.Kind == RunningKind.FeedPaused;

        NotificationCompat.Builder Base() => new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(icon)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetSilent(true)!
            .SetCategory(NotificationCompat.CategoryStopwatch)!
            .SetWhen(timer.Since.ToUnixTimeMilliseconds())!
            .SetShowWhen(!paused)!
            .SetUsesChronometer(!paused)!
            .SetContentIntent(tap)!;

        // On a locked screen: the clock and the buttons, without saying whose care it is (ADR 0007 §4).
        var hidden = Base().SetContentTitle("Coo")!.SetContentText(hiddenText)!;
        var full = Base()
            .SetContentTitle(timer.Title)!
            .SetContentText(timer.Text)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!;
        foreach (var (action, text) in actions)
        {
            var pending = QuickActionReceiver.Pending(context, action);
            hidden.AddAction(0, text, pending);
            full.AddAction(0, text, pending);
        }

        return full.SetPublicVersion(hidden.Build())!.Build()!;
    }

    private static void EnsureChannel(Context context, string name)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            return;
        // Default importance, so it sits with the other notifications, expanded with its buttons; but silent: no sound,
        // no vibration, no heads-up (Low put it collapsed under "Silent", buttons hidden).
        var channel = new NotificationChannel(ChannelId, name, NotificationImportance.Default) { LockscreenVisibility = NotificationVisibility.Private };
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        manager.CreateNotificationChannel(channel);
    }
}
