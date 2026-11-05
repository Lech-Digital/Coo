using Android.App;
using Android.Content;
using Android.OS;
using Coo.Mobile.Application.Notifications;
using Microsoft.Maui.ApplicationModel;

namespace Coo.Mobile.Platform.Notifications;

/// <summary>
/// Coo's reminders as Android alarms (ADR 0007 §3): exact (<c>setExactAndAllowWhileIdle</c>) where the parent allowed
/// "Alarms &amp; reminders", otherwise <c>setAndAllowWhileIdle</c>, which Doze may hold back for a while. What is scheduled
/// is also kept in the app's shared preferences, so the boot receiver can put the alarms back without starting the app,
/// and a reconcile can cancel what is no longer planned.
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

    public bool CanBeExact => Reminders.CanBeExact(Context);

    public void OpenExactSettings()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            return;
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionRequestScheduleExactAlarm)
                .SetData(global::Android.Net.Uri.Parse("package:" + Context.PackageName))
                .AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }
#pragma warning disable CA1031 // A phone without that screen: reminders stay inexact, nothing else changes.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Exact alarm settings not opened: {e.GetType().Name}");
        }
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

    /// <summary>The one-tap log receiver in the app (by its Java name: the app project references this one, not back).</summary>
    public const string QuickReceiver = "com.lechdigital.coo.QuickActionReceiver";
    public const string QuickAction = "com.lechdigital.coo.action.QUICK";
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

    /// <summary>Before Android 12 every app may; from 12 the parent decides, in "Alarms &amp; reminders".</summary>
    public static bool CanBeExact(Context context) =>
        !OperatingSystem.IsAndroidVersionAtLeast(31)
        || (context.GetSystemService(Context.AlarmService) is AlarmManager alarms && alarms.CanScheduleExactAlarms());

    private static void Schedule(Context context, ReminderNotice notice)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms)
            return;
        var at = notice.At.ToUnixTimeMilliseconds();
        var pending = Pending(context, notice.Id, notice);
        try
        {
            if (CanBeExact(context))
            {
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);
                return;
            }
        }
        catch (Java.Lang.SecurityException)
        {
            // Taken back between the check and the call: inexact below.
        }

        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);
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
            intent.PutExtra("title", notice.Title).PutExtra("body", notice.Body).PutExtra("buttons", WriteButtons(notice.Buttons));
        return PendingIntent.GetBroadcast(context, Code(id), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    // A notice's buttons in one string: label, action, medicine name and dose split by U+001F, buttons by U+001E.
    public static string WriteButtons(IReadOnlyList<ReminderButton>? buttons) => buttons is null ? "" : string.Join('\u001e', buttons.Select(b =>
        string.Join('\u001f', b.Label, b.Action.ToString(), b.Name ?? "", b.Dose ?? "")));

    public static IReadOnlyList<ReminderButton> ReadButtons(string? text)
    {
        var list = new List<ReminderButton>();
        foreach (var one in (text ?? "").Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = one.Split('\u001f');
            if (parts.Length == 4 && Enum.TryParse<Coo.Mobile.Application.CareLog.QuickAction>(parts[1], out var action))
                list.Add(new ReminderButton(parts[0], action, parts[2].Length == 0 ? null : parts[2], parts[3].Length == 0 ? null : parts[3]));
        }

        return list;
    }

    // One line per reminder: id, unix milliseconds, title, body, buttons, tab-separated. Texts never hold tabs or newlines.
    private static List<ReminderNotice> Load(Context context)
    {
        var text = context.GetSharedPreferences(Store, FileCreationMode.Private)?.GetString(Key, null) ?? "";
        var list = new List<ReminderNotice>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length is 4 or 5 && long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                list.Add(new ReminderNotice(parts[0], DateTimeOffset.FromUnixTimeMilliseconds(ms), parts[2], parts[3], parts.Length == 5 ? ReadButtons(parts[4]) : null));
        }

        return list;
    }

    private static void Save(Context context, IEnumerable<ReminderNotice> notices)
    {
        static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
        var text = string.Join('\n', notices.Select(n =>
            string.Join('\t', Clean(n.Id), n.At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Clean(n.Title), Clean(n.Body),
                Clean(WriteButtons(n.Buttons)))));
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
            var title = intent.GetStringExtra("title") ?? "Mochi";
            var body = intent.GetStringExtra("body") ?? "";
            Reminders.EnsureChannel(context, ChannelName.Value);

            var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
            var tap = open is null ? null : PendingIntent.GetActivity(context, Reminders.Code(id), open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var icon = context.Resources?.GetIdentifier("ic_stat_coo", "drawable", context.PackageName) is > 0 and var found ? found : context.ApplicationInfo?.Icon ?? 0;

            // On a locked screen only "Mochi reminder" shows (ADR 0007 §4): a child's care stays private.
            var hidden = new AndroidX.Core.App.NotificationCompat.Builder(context, Reminders.ChannelId)
                .SetSmallIcon(icon)!
                .SetContentTitle("Mochi")!
                .Build();
            var builder = new AndroidX.Core.App.NotificationCompat.Builder(context, Reminders.ChannelId)
                .SetSmallIcon(icon)!
                .SetContentTitle(title)!
                .SetContentText(body)!
                .SetStyle(new AndroidX.Core.App.NotificationCompat.BigTextStyle().BigText(body))!
                .SetCategory(AndroidX.Core.App.NotificationCompat.CategoryReminder)!
                .SetVisibility(AndroidX.Core.App.NotificationCompat.VisibilityPrivate)!
                .SetPublicVersion(hidden)!
                .SetAutoCancel(true)!
                .SetContentIntent(tap)!;

            // "Wet", "Given", "Bath done": logged by the app's one-tap receiver, which then clears this notification.
            var buttons = Reminders.ReadButtons(intent.GetStringExtra("buttons"));
            for (var i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                var quick = new Intent(Reminders.QuickAction)
                    .SetClassName(context.PackageName ?? "", Reminders.QuickReceiver)
                    .PutExtra("action", button.Action.ToString())
                    .PutExtra("notification", Reminders.Code(id));
                if (button.Name is not null)
                    quick.PutExtra("name", button.Name).PutExtra("dose", button.Dose);
                var pending = PendingIntent.GetBroadcast(context, Reminders.Code($"{id}:{i}"), quick, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.AddAction(0, button.Label, pending);
            }

            AndroidX.Core.App.NotificationManagerCompat.From(context)?.Notify(Reminders.Code(id), builder.Build());
        }
#pragma warning disable CA1031 // An Android callback: a reminder that cannot be shown must not crash the process.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Reminder not shown: {e.GetType().Name}");
        }
    }
}

/// <summary>
/// Puts the alarms back after a reboot or an app update, when Android has dropped them all, and again when the parent
/// allows exact alarms, so the ones waiting become exact.
/// </summary>
[BroadcastReceiver(Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, ExactAllowed])]
public sealed class ReminderBootReceiver : BroadcastReceiver
{
    private const string ExactAllowed = "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced or ExactAllowed))
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
