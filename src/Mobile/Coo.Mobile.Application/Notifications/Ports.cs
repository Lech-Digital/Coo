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
}

public interface IReminderSettingsStore
{
    Task<ReminderSettings> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(ReminderSettings settings, CancellationToken cancellationToken);
}

/// <summary>What the home-screen widget shows: the baby, and three lines of absolute times that stay true until the next update.</summary>
public sealed record WidgetContent(string Title, string Feed, string Sleep, string Diaper);

/// <summary>The home-screen widget: redrawn after every write and sync.</summary>
public interface IHomeWidget
{
    void Show(WidgetContent content);
}
