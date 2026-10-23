using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Notifications;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Notifications;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// Settings → Reminders (ADR 0007 §4): everything off until the parent turns it on, and the notification permission is
/// asked for the first time a reminder is turned on, never at launch.
/// </summary>
public sealed partial class RemindersViewModel(
    IReminderSettingsStore store,
    IReminderScheduler scheduler,
    IDevicePreferences preferences,
    ReminderRefresher refresher,
    IBillingService billing) : ObservableObject
{
    [ObservableProperty]
    private ReminderSettings _settings = ReminderSettings.Off;

    [ObservableProperty]
    private string _quietHint = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _canBeExact = true;

    /// <summary>The nap reminder follows the personal prediction, which is Premium (ADR 0008 §7).</summary>
    public bool IsPremium => billing.IsPremiumCached;

    /// <summary>Exact alarms are worth asking for once something is on.</summary>
    public bool ShowExact => !CanBeExact && Settings.AnyOn;

    public static string LeadLabel(int minutes) => Strings.Format("Reminders.Minutes", minutes);

    /// <summary>The feeding choices: Off, then each interval.</summary>
    public static IReadOnlyList<TimeSpan?> FeedChoices { get; } = [null, .. ReminderSettings.FeedIntervals.Select(i => (TimeSpan?)i)];

    public static string FeedLabel(TimeSpan? every) => every is { } e
        ? Strings.Format("Reminders.Hours", e.TotalHours.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
        : Strings.Get("Reminders.Off");

    public async Task LoadAsync()
    {
        Settings = await store.GetAsync(CancellationToken.None);
        CanBeExact = scheduler.CanBeExact;
        // Back from Android's settings with exact alarms allowed: schedule them again, on the minute.
        await refresher.RefreshAsync();
        var quiet = (await preferences.GetAppearanceAsync(CancellationToken.None)).QuietHours;
        QuietHint = Strings.Format("Reminders.FeedAtNightHint", quiet.Start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            quiet.End.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private Task SetFeedEvery(TimeSpan? every) => ApplyAsync(Settings with { FeedEvery = every });

    [RelayCommand]
    private Task ToggleFeedAtNight() => ApplyAsync(Settings with { FeedAtNight = !Settings.FeedAtNight });

    [RelayCommand]
    private Task ToggleTimer() => ApplyAsync(Settings with { TimerRunning = !Settings.TimerRunning });

    [RelayCommand]
    private Task ToggleNap() => ApplyAsync(Settings with { NapWindow = !Settings.NapWindow });

    [RelayCommand]
    private Task SetNapLead(int minutes) => ApplyAsync(Settings with { NapLeadMinutes = minutes });

    [RelayCommand]
    private void AllowExact() => scheduler.OpenExactSettings();

    private async Task ApplyAsync(ReminderSettings next)
    {
        Message = "";
        // Turning something on is the moment to ask (ADR 0007 §4); a "no" keeps the choice but says why nothing arrives.
        if (IsTurningOn(next) && !await scheduler.EnsurePermissionAsync())
            Message = Strings.Get("Reminders.Denied");

        Settings = next;
        await store.SetAsync(next, CancellationToken.None);
        await refresher.RefreshAsync();
    }

    private bool IsTurningOn(ReminderSettings next) =>
        (next.FeedEvery is not null && Settings.FeedEvery is null) || (next.TimerRunning && !Settings.TimerRunning) || (next.NapWindow && !Settings.NapWindow);
}
