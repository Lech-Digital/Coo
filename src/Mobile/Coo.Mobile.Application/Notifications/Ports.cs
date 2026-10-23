namespace Coo.Mobile.Application.Notifications;

/// <summary>A reminder ready for Android: its stable ID, when, and the text, composed on the phone (ADR 0007 §2).</summary>
public sealed record ReminderNotice(string Id, DateTimeOffset At, string Title, string Body);

/// <summary>Android's alarms for Coo's reminders (ADR 0007 §6).</summary>
public interface IReminderScheduler
{
    /// <summary>Makes what is scheduled exactly <paramref name="notices"/>: new ones added, changed ones moved, the rest cancelled.</summary>
    Task ReplaceAllAsync(IReadOnlyList<ReminderNotice> notices, CancellationToken cancellationToken);

    /// <summary>Asks for the notification permission (Android 13+) if needed; false when the parent said no.</summary>
    Task<bool> EnsurePermissionAsync();

    /// <summary>Reminders arrive on the minute: Android lets Coo set exact alarms (ADR 0007 §3).</summary>
    bool CanBeExact { get; }

    /// <summary>Opens Android's "Alarms &amp; reminders" setting for Coo, where exact alarms are allowed.</summary>
    void OpenExactSettings();
}

public interface IReminderSettingsStore
{
    Task<ReminderSettings> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(ReminderSettings settings, CancellationToken cancellationToken);
}

/// <summary>What is running now, as the widgets and the timer notification show it.</summary>
public enum RunningKind
{
    FeedLeft,
    FeedRight,
    FeedPaused,
    Sleep,
    Pump,
}

/// <summary>A running timer: <paramref name="Since"/> is its start, so a clock on the notification or widget counts by itself.</summary>
/// <param name="Text">The words under the title ("Started at 23:10").</param>
public sealed record RunningTimer(RunningKind Kind, DateTimeOffset Since, string Title, string Text);

/// <summary>
/// What the home-screen widgets show (all of them, from one refresh): three lines of absolute times that stay true until
/// the next update, the instants their clocks count from, what is running, the next likely sleep, and every label in the
/// app's language, so a widget redrawn by the launcher needs nothing from the app.
/// </summary>
/// <param name="Labels">Button and caption texts by <see cref="WidgetLabels"/> key.</param>
public sealed record WidgetContent(
    string Title,
    string Feed,
    string Sleep,
    string Diaper,
    DateTimeOffset? LastFeedAt = null,
    DateTimeOffset? AwakeSince = null,
    DateTimeOffset? AsleepSince = null,
    IReadOnlyList<RunningTimer>? Running = null,
    string NextSleep = "",
    IReadOnlyDictionary<string, string>? Labels = null,
    bool HasBaby = true);

/// <summary>The keys of <see cref="WidgetContent.Labels"/>.</summary>
public static class WidgetLabels
{
    public const string FeedLeft = "feed-left";
    public const string FeedRight = "feed-right";
    public const string Switch = "switch";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Wet = "wet";
    public const string Dirty = "dirty";
    public const string Sleep = "sleep";
    public const string WokeUp = "woke-up";
    public const string Pump = "pump";
    public const string StopPump = "stop-pump";
    public const string LastFeed = "last-feed";
    public const string Awake = "awake";
    public const string Asleep = "asleep";
    public const string Tell = "tell";
    public const string Open = "open";
    public const string Channel = "channel";
    public const string TimerHidden = "timer-hidden";
}

/// <summary>The home-screen widgets: redrawn after every write and sync.</summary>
public interface IHomeWidget
{
    void Show(WidgetContent content);
}

/// <summary>
/// Running timers in the notification bar, with their buttons (switch side, stop, woke up), so a feed is handled from
/// the lock screen. Shown while something runs, gone when nothing does.
/// </summary>
public interface ITimerNotifications
{
    void Show(IReadOnlyList<RunningTimer> running, IReadOnlyDictionary<string, string> labels);
}
