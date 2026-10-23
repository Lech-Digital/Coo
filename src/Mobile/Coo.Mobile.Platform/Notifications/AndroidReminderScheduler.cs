using Android.App;
using Android.Content;
using Android.OS;
using Coo.Mobile.Application.Notifications;
using Microsoft.Maui.ApplicationModel;

namespace Coo.Mobile.Platform.Notifications;

/// <summary>
/// Coo's reminders as Android alarms (ADR 0007 §3): <c>setAndAllowWhileIdle</c>, so they fire in Doze, a few minutes late
/// at worst. What is scheduled is also kept in the app's shared preferences, so the boot receiver can put the alarms
/// back without starting the app, and a reconcile can cancel what is no longer planned.
/// </summary>
internal sealed class AndroidReminderScheduler : IReminderScheduler
{
    private static Context Context => global::Android.App.Application.Context;

    public Task ReplaceAllAsync(IReadOnlyList<ReminderNotice> notices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notices);
        Reminders.EnsureChannel(Context, ChannelName.Value);
        Reminders.Replace(Context, notices);
        return Task.CompletedTask;
    }

    public async Task<bool> EnsurePermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return true;
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            return status == PermissionStatus.Granted;
        });
    }
}

/// <summary>The stored schedule and the alarms, shared by the scheduler and the receivers (which run without the app's services).</summary>
internal static class Reminders
{
    public const string Action = "com.lechdigital.coo.action.REMINDER";
    public const string ChannelId = "reminders";
    private const string Store = "coo.reminders";
    private const string Key = "scheduled";

    public static void Replace(Context context, IReadOnlyList<ReminderNotice> notices)
    {
        var old = Load(context);
        foreach (var gone in old.Where(o => notices.All(n => n.Id != o.Id)))
            Cancel(context, gone.Id);
        foreach (var notice in notices)
            Schedule(context, notice);
        Save(context, notices);
    }

    /// <summary>After a reboot or an app update Android has dropped every alarm: the ones still to come go back.</summary>
    public static void Restore(Context context, DateTimeOffset now)
    {
        var left = Load(context).Where(n => n.At > now).ToList();
        foreach (var notice in left)
            Schedule(context, notice);
        Save(context, left);
    }

    /// <summary>A fired reminder leaves the stored schedule.</summary>
    public static void Forget(Context context, string id) => Save(context, [.. Load(context).Where(n => n.Id != id)]);

    /// <summary>A stable request code per reminder ID: <see cref="string.GetHashCode()"/> changes between processes.</summary>
    public static int Code(string id)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in id)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private static void Schedule(Context context, ReminderNotice notice)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms)
            return;
        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, notice.At.ToUnixTimeMilliseconds(), Pending(context, notice.Id, notice));
    }

    private static void Cancel(Context context, string id)
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms)
            alarms.Cancel(Pending(context, id, null));
    }

    private static PendingIntent Pending(Context context, string id, ReminderNotice? notice)
    {
        var intent = new Intent(context, typeof(ReminderReceiver)).SetAction(Action).PutExtra("id", id);
        if (notice is not null)
            intent.PutExtra("title", notice.Title).PutExtra("body", notice.Body);
        return PendingIntent.GetBroadcast(context, Code(id), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    // One line per reminder: id, unix milliseconds, title, body, tab-separated. Texts never hold tabs or newlines.
    private static List<ReminderNotice> Load(Context context)
    {
        var text = context.GetSharedPreferences(Store, FileCreationMode.Private)?.GetString(Key, null) ?? "";
        var list = new List<ReminderNotice>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 4 && long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                list.Add(new ReminderNotice(parts[0], DateTimeOffset.FromUnixTimeMilliseconds(ms), parts[2], parts[3]));
        }

        return list;
    }

    private static void Save(Context context, IEnumerable<ReminderNotice> notices)
    {
        static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
        var text = string.Join('\n', notices.Select(n =>
            string.Join('\t', Clean(n.Id), n.At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Clean(n.Title), Clean(n.Body))));
        context.GetSharedPreferences(Store, FileCreationMode.Private)?.Edit()?.PutString(Key, text)?.Apply();
    }

    /// <summary>The reminders channel (ADR 0007 §4); Android keeps the first definition, so creating it again is harmless.</summary>
    public static void EnsureChannel(Context context, string name)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            return;
        var channel = new NotificationChannel(ChannelId, name, NotificationImportance.High) { LockscreenVisibility = NotificationVisibility.Private };
        manager.CreateNotificationChannel(channel);
    }
}

/// <summary>An alarm went off: shows the reminder. The text was composed when it was planned, in the app's language.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.GetStringExtra("id") is not { } id)
            return;
        try
        {
            Reminders.Forget(context, id);
            var title = intent.GetStringExtra("title") ?? "Coo";
            var body = intent.GetStringExtra("body") ?? "";
            Reminders.EnsureChannel(context, ChannelName.Value);

            var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
            var tap = open is null ? null : PendingIntent.GetActivity(context, Reminders.Code(id), open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var icon = context.Resources?.GetIdentifier("ic_stat_coo", "drawable", context.PackageName) is > 0 and var found ? found : context.ApplicationInfo?.Icon ?? 0;

            // On a locked screen only "Coo reminder" shows (ADR 0007 §4): a child's care stays private.
            var hidden = new AndroidX.Core.App.NotificationCompat.Builder(context, Reminders.ChannelId)
                .SetSmallIcon(icon)!
                .SetContentTitle("Coo")!
                .Build();
            var notification = new AndroidX.Core.App.NotificationCompat.Builder(context, Reminders.ChannelId)
                .SetSmallIcon(icon)!
                .SetContentTitle(title)!
                .SetContentText(body)!
                .SetStyle(new AndroidX.Core.App.NotificationCompat.BigTextStyle().BigText(body))!
                .SetCategory(AndroidX.Core.App.NotificationCompat.CategoryReminder)!
                .SetVisibility(AndroidX.Core.App.NotificationCompat.VisibilityPrivate)!
                .SetPublicVersion(hidden)!
                .SetAutoCancel(true)!
                .SetContentIntent(tap)!
                .Build();
            AndroidX.Core.App.NotificationManagerCompat.From(context)?.Notify(Reminders.Code(id), notification);
        }
#pragma warning disable CA1031 // An Android callback: a reminder that cannot be shown must not crash the process.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Reminder not shown: {e.GetType().Name}");
        }
    }
}

/// <summary>Puts the alarms back after a reboot or an app update, when Android has dropped them all.</summary>
[BroadcastReceiver(Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class ReminderBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || (intent?.Action != Intent.ActionBootCompleted && intent?.Action != Intent.ActionMyPackageReplaced))
            return;
        try
        {
            // A receiver runs without the app's services, so without its clock: Android's wall clock it is.
            Reminders.Restore(context, DateTimeOffset.FromUnixTimeMilliseconds(Java.Lang.JavaSystem.CurrentTimeMillis()));
        }
#pragma warning disable CA1031 // An Android callback: nothing may escape into the system.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Reminders not restored: {e.GetType().Name}");
        }
    }
}

/// <summary>The channel's name in Android's settings, set by the app in its language when it starts.</summary>
public static class ChannelName
{
    public static string Value { get; set; } = "Reminders";
}
