using System.Globalization;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Widget;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Notifications;

namespace Coo.Mobile;

/// <summary>
/// The home-screen widgets' adapter: the app hands it what to show after every write and sync, it keeps that in shared
/// preferences (a widget added later, or redrawn by the launcher, shows it without the app) and redraws every widget:
/// the glance (last feed, sleep, diaper, Tell Coo), quick log (six one-tap buttons) and the clocks.
/// </summary>
public sealed class HomeWidget : IHomeWidget
{
    internal const string Store = "coo.widget";

    public void Show(WidgetContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var context = global::Android.App.Application.Context;
        var edit = context.GetSharedPreferences(Store, FileCreationMode.Private)?.Edit();
        if (edit is null)
            return;
        static long Ms(DateTimeOffset? at) => at?.ToUnixTimeMilliseconds() ?? -1;
        edit.Clear();
        edit.PutString("title", content.Title);
        edit.PutString("feed", content.Feed);
        edit.PutString("sleep", content.Sleep);
        edit.PutString("diaper", content.Diaper);
        edit.PutString("next", content.NextSleep);
        edit.PutBoolean("has_baby", content.HasBaby);
        edit.PutLong("last_feed", Ms(content.LastFeedAt));
        edit.PutLong("awake", Ms(content.AwakeSince));
        edit.PutLong("asleep", Ms(content.AsleepSince));
        edit.PutString("running", string.Join(',', (content.Running ?? []).Select(r => r.Kind.ToString())));
        edit.PutString("running_title", content.Running is [var first, ..] ? first.Title : "");
        foreach (var (key, text) in content.Labels ?? new Dictionary<string, string>())
            edit.PutString("label:" + key, text);
        edit.Apply();
        Redraw(context);
    }

    internal static void Redraw(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
            return;
        void Draw<TProvider>(Func<Context, RemoteViews> views)
        {
            var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(TProvider))));
            if (ids is { Length: > 0 })
                manager.UpdateAppWidget(ids, views(context));
        }

        Draw<CooWidgetProvider>(Glance);
        Draw<CooQuickWidgetProvider>(Quick);
        Draw<CooTimerWidgetProvider>(Clocks);
    }

    private sealed class Stored(Context context)
    {
        private readonly ISharedPreferences? _store = context.GetSharedPreferences(Store, FileCreationMode.Private);

        public string Text(string key, string fallback = "") => _store?.GetString(key, null) ?? fallback;

        public string Label(string key, string fallback) => Text("label:" + key, fallback);

        public DateTimeOffset? At(string key) => _store?.GetLong(key, -1) is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

        public bool HasBaby => _store?.GetBoolean("has_baby", false) ?? false;

        public IReadOnlyList<RunningKind> Running => [.. Text("running").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(k => Enum.TryParse<RunningKind>(k, out var kind) ? (RunningKind?)kind : null).OfType<RunningKind>()];
    }

    internal static RemoteViews Glance(Context context)
    {
        var stored = new Stored(context);
        var views = new RemoteViews(context.PackageName, Resource.Layout.coo_widget);
        views.SetTextViewText(Resource.Id.widget_title, stored.Text("title", "Coo"));
        views.SetTextViewText(Resource.Id.widget_feed, stored.Text("feed", stored.Label(WidgetLabels.Open, "Open Coo")));
        Line(views, Resource.Id.widget_sleep, stored.Text("sleep"));
        views.SetTextViewText(Resource.Id.widget_diaper, stored.Text("diaper"));
        Line(views, Resource.Id.widget_next, stored.Text("next"));
        views.SetTextViewText(Resource.Id.widget_tell, stored.Label(WidgetLabels.Tell, "Tell Coo"));
        views.SetOnClickPendingIntent(Resource.Id.widget_root, OpenApp(context, 1));
        var tell = new Intent(context, typeof(MainActivity)).SetAction(VoiceShortcut.Action).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        views.SetOnClickPendingIntent(Resource.Id.widget_tell,
            PendingIntent.GetActivity(context, 2, tell, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
        return views;
    }

    /// <summary>
    /// Six buttons whose job follows what runs: a feed gives Switch, Pause and Stop; a paused one Left, Right and Stop;
    /// a sleep turns Sleep into Woke up; pumping turns Pump into Stop pump. Without a baby, every button opens Coo.
    /// </summary>
    internal static RemoteViews Quick(Context context)
    {
        var stored = new Stored(context);
        var running = stored.Running;
        var feeding = running.Any(k => k is RunningKind.FeedLeft or RunningKind.FeedRight);
        var paused = running.Contains(RunningKind.FeedPaused);
        var sleeping = running.Contains(RunningKind.Sleep);
        var pumping = running.Contains(RunningKind.Pump);
        string L(string key, string fallback) => stored.Label(key, fallback);

        (QuickAction Action, string Text, string Kind)[] slots =
        [
            feeding ? (QuickAction.SwitchSide, L(WidgetLabels.Switch, "Switch"), "feed") : (QuickAction.FeedLeft, L(WidgetLabels.FeedLeft, "Left"), "feed"),
            feeding ? (QuickAction.PauseFeed, L(WidgetLabels.Pause, "Pause"), "feed") : (QuickAction.FeedRight, L(WidgetLabels.FeedRight, "Right"), "feed"),
            feeding || paused ? (QuickAction.StopFeed, L(WidgetLabels.Stop, "Stop"), "feed")
                : sleeping ? (QuickAction.WokeUp, L(WidgetLabels.WokeUp, "Woke up"), "sleep") : (QuickAction.Sleep, L(WidgetLabels.Sleep, "Sleep"), "sleep"),
            (QuickAction.Wet, L(WidgetLabels.Wet, "Wet"), "diaper"),
            (QuickAction.Dirty, L(WidgetLabels.Dirty, "Dirty"), "diaper"),
            pumping ? (QuickAction.StopPump, L(WidgetLabels.StopPump, "Stop pump"), "pump") : (QuickAction.Pump, L(WidgetLabels.Pump, "Pump"), "pump"),
        ];

        var views = new RemoteViews(context.PackageName, Resource.Layout.coo_widget_quick);
        var title = stored.Text("running_title");
        views.SetTextViewText(Resource.Id.quick_title, title.Length > 0 ? title : stored.Text("title", "Coo"));
        views.SetOnClickPendingIntent(Resource.Id.quick_root, OpenApp(context, 3));
        int[] ids = [Resource.Id.quick_1, Resource.Id.quick_2, Resource.Id.quick_3, Resource.Id.quick_4, Resource.Id.quick_5, Resource.Id.quick_6];
        for (var i = 0; i < slots.Length; i++)
        {
            var (action, text, kind) = slots[i];
            views.SetTextViewText(ids[i], text);
            var (background, ink) = kind switch
            {
                "sleep" => (Resource.Drawable.widget_chip_sleep, Resource.Color.widget_sleep_ink),
                "diaper" => (Resource.Drawable.widget_chip_diaper, Resource.Color.widget_diaper_ink),
                "pump" => (Resource.Drawable.widget_chip_pump, Resource.Color.widget_pump_ink),
                _ => (Resource.Drawable.widget_chip_feed, Resource.Color.widget_feed_ink),
            };
            views.SetInt(ids[i], "setBackgroundResource", background);
            views.SetTextColor(ids[i], new global::Android.Graphics.Color(context.GetColor(ink)));
            views.SetOnClickPendingIntent(ids[i], stored.HasBaby ? QuickActionReceiver.Pending(context, action) : OpenApp(context, 10 + i));
        }

        return views;
    }

    /// <summary>Two clocks that count by themselves: since the last feed started, and awake (or asleep) for.</summary>
    internal static RemoteViews Clocks(Context context)
    {
        var stored = new Stored(context);
        var views = new RemoteViews(context.PackageName, Resource.Layout.coo_widget_timer);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Java.Lang.JavaSystem.CurrentTimeMillis());
        var elapsedNow = SystemClock.ElapsedRealtime();

        void Clock(int label, int clock, int empty, string caption, DateTimeOffset? since)
        {
            views.SetTextViewText(label, caption);
            if (since is { } start && start <= now)
            {
                // A chronometer counts from a moment on Android's monotonic clock: now there, minus how long ago it was.
                views.SetChronometer(clock, elapsedNow - (long)(now - start).TotalMilliseconds, null, true);
                views.SetViewVisibility(clock, global::Android.Views.ViewStates.Visible);
                views.SetViewVisibility(empty, global::Android.Views.ViewStates.Gone);
            }
            else
            {
                views.SetChronometer(clock, elapsedNow, null, false);
                views.SetViewVisibility(clock, global::Android.Views.ViewStates.Gone);
                views.SetViewVisibility(empty, global::Android.Views.ViewStates.Visible);
            }
        }

        Clock(Resource.Id.feed_label, Resource.Id.feed_clock, Resource.Id.feed_empty, stored.Label(WidgetLabels.LastFeed, "Last feed"), stored.At("last_feed"));
        var asleep = stored.At("asleep");
        Clock(Resource.Id.sleep_label, Resource.Id.sleep_clock, Resource.Id.sleep_empty,
            asleep is null ? stored.Label(WidgetLabels.Awake, "Awake") : stored.Label(WidgetLabels.Asleep, "Asleep"), asleep ?? stored.At("awake"));
        views.SetOnClickPendingIntent(Resource.Id.timer_root, OpenApp(context, 4));
        return views;
    }

    private static void Line(RemoteViews views, int id, string text)
    {
        views.SetTextViewText(id, text);
        views.SetViewVisibility(id, text.Length == 0 ? global::Android.Views.ViewStates.Gone : global::Android.Views.ViewStates.Visible);
    }

    private static PendingIntent OpenApp(Context context, int code)
    {
        var open = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(context, code, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
}

/// <summary>The glance widget as Android knows it: drawn from what the app last stored.</summary>
[BroadcastReceiver(Exported = true, Label = "@string/widget_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/coo_widget_info")]
public sealed class CooWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null && appWidgetManager is not null && appWidgetIds is not null)
            appWidgetManager.UpdateAppWidget(appWidgetIds, HomeWidget.Glance(context));
    }
}

/// <summary>Quick log: one tap logs a feed, a diaper, a sleep or a pump, without opening Coo.</summary>
[BroadcastReceiver(Exported = true, Label = "@string/widget_quick_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/coo_widget_quick_info")]
public sealed class CooQuickWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null && appWidgetManager is not null && appWidgetIds is not null)
            appWidgetManager.UpdateAppWidget(appWidgetIds, HomeWidget.Quick(context));
    }
}

/// <summary>The clocks: since the last feed, and awake or asleep for.</summary>
[BroadcastReceiver(Exported = true, Label = "@string/widget_timer_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/coo_widget_timer_info")]
public sealed class CooTimerWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null && appWidgetManager is not null && appWidgetIds is not null)
            appWidgetManager.UpdateAppWidget(appWidgetIds, HomeWidget.Clocks(context));
    }
}

/// <summary>
/// A widget's or a timer notification's button: logs through <see cref="QuickActions"/> in the app's process (started
/// for it if need be), then refreshes widgets, notification and reminders, says what was logged, and tells an open
/// Today to reload.
/// </summary>
// Named, so a reminder's buttons (in Coo.Mobile.Platform, which cannot see this type) can reach it by its Java name.
[BroadcastReceiver(Exported = false, Name = "com.lechdigital.coo.QuickActionReceiver")]
public sealed class QuickActionReceiver : BroadcastReceiver
{
    private const string Act = "com.lechdigital.coo.action.QUICK";

    public static PendingIntent Pending(Context context, QuickAction action)
    {
        var intent = new Intent(context, typeof(QuickActionReceiver)).SetAction(Act).PutExtra("action", action.ToString());
        return PendingIntent.GetBroadcast(context, 100 + (int)action, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Act || !Enum.TryParse<QuickAction>(intent.GetStringExtra("action"), out var action))
            return;
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                if (IPlatformApplication.Current?.Services is not { } services)
                    return;
                await Background.PrepareAsync(services);
                // From a reminder's button: the reminder goes once answered, and "Given" carries the medicine's name and dose.
                if (intent.GetIntExtra("notification", 0) is not 0 and var shown)
                    AndroidX.Core.App.NotificationManagerCompat.From(context)?.Cancel(shown);
                (string, string?)? medicine = intent.GetStringExtra("name") is { } name ? (name, intent.GetStringExtra("dose")) : null;
                var result = await services.GetRequiredService<QuickActions>().ExecuteAsync(action, CancellationToken.None, medicine);
                await services.GetRequiredService<Coo.Mobile.Presentation.Notifications.ReminderRefresher>().RefreshAsync();
                var text = Coo.Mobile.Presentation.Notifications.QuickActionText.Describe(result);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Toast.MakeText(context, text, ToastLength.Short)?.Show();
                    try
                    {
                        services.GetRequiredService<Coo.Mobile.Presentation.Today.UndoService>().ExternalChange();
                    }
#pragma warning disable CA1031 // Today may not exist in this process; the next refresh shows the entry anyway.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                });
            }
#pragma warning disable CA1031 // An Android callback: a failed one-tap log must not crash the process.
            catch (Exception e)
#pragma warning restore CA1031
            {
                global::Android.Util.Log.Warn("Coo", $"Quick action failed: {e.GetType().Name}");
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}

/// <summary>What a process started for a widget, a notification or a background job needs before it composes words.</summary>
internal static class Background
{
    public static async Task PrepareAsync(IServiceProvider services)
    {
        var language = services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>();
        if (!language.IsStarted)
            await language.StartAsync(CultureInfo.CurrentUICulture, CancellationToken.None);
        Coo.Mobile.Platform.Notifications.ChannelName.Value = Coo.Mobile.Presentation.Localization.Strings.Get("Reminders.Title");
    }
}
