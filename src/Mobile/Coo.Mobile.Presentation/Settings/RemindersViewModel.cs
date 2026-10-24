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

    /// <summary>Set by the page: Coo's own explanation before Android asks for notifications; false is "Not now".</summary>
    public Func<Task<bool>>? PrimeNotificationsAsync { get; set; }

    public static string LeadLabel(int minutes) => Strings.Format("Reminders.Minutes", minutes);

    /// <summary>The feeding choices: Off, then each interval.</summary>
    public static IReadOnlyList<TimeSpan?> FeedChoices { get; } = [null, .. ReminderSettings.FeedIntervals.Select(i => (TimeSpan?)i)];

    public static string FeedLabel(TimeSpan? every) => every is { } e
        ? Strings.Format("Reminders.Hours", e.TotalHours.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
        : Strings.Get("Reminders.Off");

    /// <summary>The diaper choices: Off, then each interval.</summary>
    public static IReadOnlyList<TimeSpan?> DiaperChoices { get; } = [null, .. ReminderSettings.DiaperIntervals.Select(i => (TimeSpan?)i)];

    /// <summary>The bath choices: Off, then every 1, 2, 3 or 7 days.</summary>
    public static IReadOnlyList<int?> BathChoices { get; } = [null, .. ReminderSettings.BathIntervals.Select(i => (int?)i)];

    public static string BathLabel(int? days) => days switch
    {
        null => Strings.Get("Reminders.Off"),
        1 => Strings.Get("Reminders.Daily"),
        7 => Strings.Get("Reminders.Weekly"),
        _ => Strings.Format("Reminders.EveryDays", days),
    };

    /// <summary>"09:00, 21:00 · until Fri 30 Oct" or "09:00 · every day".</summary>
    public static string MedicineLabel(MedicineReminder medicine)
    {
        ArgumentNullException.ThrowIfNull(medicine);
        var times = string.Join(", ", medicine.Times.Order().Select(t => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        var days = medicine.Until is { } until
            ? Strings.Format("Reminders.Until", until.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture))
            : Strings.Get("Reminders.EveryDay");
        return $"{times} · {days}";
    }

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
    private Task SetDiaperEvery(TimeSpan? every) => ApplyAsync(Settings with { DiaperEvery = every });

    [RelayCommand]
    private Task ToggleDiaperAtNight() => ApplyAsync(Settings with { DiaperAtNight = !Settings.DiaperAtNight });

    [RelayCommand]
    private Task SetBathEvery(int? days) => ApplyAsync(Settings with { BathEveryDays = days });

    public Task SetBathAtAsync(TimeOnly at) => ApplyAsync(Settings with { BathAt = at });

    /// <summary>Adds a medicine reminder (<paramref name="index"/> -1) or replaces the one at <paramref name="index"/>.</summary>
    public Task SaveMedicineAsync(int index, MedicineReminder medicine)
    {
        ArgumentNullException.ThrowIfNull(medicine);
        var list = Settings.MedicineList.ToList();
        if (index >= 0 && index < list.Count)
            list[index] = medicine;
        else
            list.Add(medicine);
        return ApplyAsync(Settings with { Medicines = list });
    }

    public Task RemoveMedicineAsync(int index)
    {
        var list = Settings.MedicineList.ToList();
        if (index < 0 || index >= list.Count)
            return Task.CompletedTask;
        list.RemoveAt(index);
        return ApplyAsync(Settings with { Medicines = list });
    }

    [RelayCommand]
    private void AllowExact() => scheduler.OpenExactSettings();

    private async Task ApplyAsync(ReminderSettings next)
    {
        Message = "";
        // Turning something on is the moment to ask (ADR 0007 §4); a "no" keeps the choice but says why nothing arrives.
        if (IsTurningOn(next) && (PrimeNotificationsAsync is { } prime && !await prime() || !await scheduler.EnsurePermissionAsync()))
            Message = Strings.Get("Reminders.Denied");

        Settings = next;
        await store.SetAsync(next, CancellationToken.None);
        await refresher.RefreshAsync();
    }

    private bool IsTurningOn(ReminderSettings next) =>
        (next.FeedEvery is not null && Settings.FeedEvery is null) || (next.TimerRunning && !Settings.TimerRunning) || (next.NapWindow && !Settings.NapWindow)
        || (next.DiaperEvery is not null && Settings.DiaperEvery is null) || (next.BathEveryDays is not null && Settings.BathEveryDays is null)
        || next.MedicineList.Count > Settings.MedicineList.Count;
}
