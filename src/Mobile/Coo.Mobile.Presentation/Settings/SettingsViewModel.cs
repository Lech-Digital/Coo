using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>One "What you log" switch (Spec 009 §3.2). The last visible kind cannot be switched off.</summary>
public sealed record KindToggle(string Code, string Name, bool IsShown, bool CanChange);

/// <summary>The Settings tab (Spec 009 §3). It knows nothing about MAUI.</summary>
public sealed partial class SettingsViewModel(
    IHouseholdStore households, ChangeVolumeUnit changeUnit, SetHiddenKinds setHidden, ThemeController theme,
    LanguageController language, IAboutInfo about, IExternalLauncher launcher, TimeProvider time) : ObservableObject
{
    public const string FeedbackAddress = "andrzej@lechdigital.nl";
    public static readonly Uri PrivacyUrl = new("https://coo.lechdigital.nl/privacy");

    private static readonly (string Code, string NameKey)[] KindNames =
        [("feeding", "Kind.Feed"), ("diaper", "Kind.Diaper"), ("sleep", "Kind.Sleep"), ("pump", "Kind.Pump")];

    private HouseholdSettings? _household;

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private string _babyLine = "";

    [ObservableProperty]
    private IReadOnlyList<KindToggle> _kinds = [];

    [ObservableProperty]
    private bool _isOunces;

    [ObservableProperty]
    private AppearanceMode _mode;

    [ObservableProperty]
    private TimeSpan _quietStart;

    [ObservableProperty]
    private TimeSpan _quietEnd;

    [ObservableProperty]
    private string _languageLabel = "";

    [ObservableProperty]
    private string _versionLine = "";

    [ObservableProperty]
    private string _toast = "";

    public IReadOnlyList<LanguageOption> Languages => LanguageRule.Options(language.SystemCulture);

    public async Task RefreshAsync()
    {
        _household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (_household is null)
            return;
        var babies = await households.ListBabiesAsync(_household.Id, CancellationToken.None);
        if (babies.Count > 0)
        {
            var baby = babies[0];
            BabyName = baby.Name;
            BabyLine = baby.IsBorn
                ? Formats.Age(baby, DateOnly.FromDateTime(time.GetLocalNow().DateTime))
                : Strings.Get("Expecting.Eyebrow");
        }

        var shown = KindNames.Count(k => Today.KindVisibility.IsShown(_household, k.Code));
        Kinds = [.. KindNames.Select(k =>
        {
            var isShown = Today.KindVisibility.IsShown(_household, k.Code);
            return new KindToggle(k.Code, Strings.Get(k.NameKey), isShown, CanChange: !isShown || shown > 1);
        })];
        IsOunces = _household.VolumeUnit == VolumeUnit.Ounces;
        Mode = theme.Settings.Mode;
        (QuietStart, QuietEnd) = (theme.Settings.QuietHours.Start.ToTimeSpan(), theme.Settings.QuietHours.End.ToTimeSpan());
        LanguageLabel = LanguageRule.Label(language.Preference, language.SystemCulture);
        var (version, commit) = AppVersionText.Parse(about.Informational);
        VersionLine = Strings.Format("Settings.Version", version, commit);
    }

    [RelayCommand]
    private async Task SetUnit(VolumeUnit unit)
    {
        if (_household is null)
            return;
        var result = await changeUnit.ExecuteAsync(_household, unit, CancellationToken.None);
        if (result.IsFailure)
            Toast = Strings.Error(result.Error!.Code);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleKind(KindToggle toggle)
    {
        if (_household is null || !toggle.CanChange)
            return;
        var result = await setHidden.ExecuteAsync(_household, EntryKindCode.From(toggle.Code).Value, hidden: toggle.IsShown, CancellationToken.None);
        if (result.IsFailure)
            Toast = Strings.Error(result.Error!.Code);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetMode(AppearanceMode mode)
    {
        await theme.SetAsync(theme.Settings with { Mode = mode, Override = null }, CancellationToken.None);
        Mode = mode;
    }

    [RelayCommand]
    private Task SaveQuietHours() =>
        theme.SetAsync(theme.Settings with { QuietHours = new QuietHours(TimeOnly.FromTimeSpan(QuietStart), TimeOnly.FromTimeSpan(QuietEnd)), Override = null }, CancellationToken.None);

    /// <summary>The host rebuilds its pages when the language in effect changes (LanguageController.Changed).</summary>
    [RelayCommand]
    private Task ChooseLanguage(LanguageOption option) => language.SetAsync(option.Preference, CancellationToken.None);

    [RelayCommand]
    private async Task SendFeedback()
    {
        var (version, commit) = AppVersionText.Parse(about.Informational);
        var body = Strings.Format("Settings.FeedbackBody", version, commit, about.OsVersion);
        if (await launcher.TryComposeEmailAsync(FeedbackAddress, Strings.Get("Settings.FeedbackSubject"), body))
            return;
        await launcher.CopyAsync(FeedbackAddress);
        Toast = Strings.Format("Settings.FeedbackCopied", FeedbackAddress);
    }

    [RelayCommand]
    private Task OpenPrivacy() => launcher.OpenBrowserAsync(PrivacyUrl);
}
