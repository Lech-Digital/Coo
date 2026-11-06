using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Log;

/// <summary>
/// Stats → Sleep pattern (owner, 2026-11-06): the last 7, 14 or 30 days as columns side by side, noon to noon, with
/// sleep and feeds, so the rhythm shows by eye; and in words, when her longest sleep usually starts and how long it lasts.
/// </summary>
public sealed partial class SleepPatternViewModel(IHouseholdStore households, GetSleepPattern pattern, IClockFormat clock, TimeProvider time) : ObservableObject
{
    public static readonly IReadOnlyList<int> Periods = [7, 14, 30];

    [ObservableProperty]
    private int _days = 7;

    [ObservableProperty]
    private SleepPattern? _pattern;

    /// <summary>"Her longest sleep usually starts around 7:10 PM and lasts about 5 h 40 m", or how to get there.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _feedsShown = true;

    [ObservableProperty]
    private bool _diapersShown = true;

    [RelayCommand]
    private async Task SetDaysAsync(int days)
    {
        if (Days == days)
            return;
        Days = days;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var baby = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        if (baby is null || !baby.IsBorn)
        {
            (Pattern, Summary) = (null, "");
            return;
        }

        FeedsShown = KindVisibility.IsShown(household, "feeding");
        DiapersShown = KindVisibility.IsShown(household, "diaper");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var result = await pattern.ExecuteAsync(household.Id, baby.Id, Days, zone, time.GetLocalNow(), CancellationToken.None);
        Pattern = result;
        Summary = Describe(result, baby.Name, new Formats(clock.Is24Hour, household.VolumeUnit));
    }

    /// <summary>The pattern in one sentence; how many days it needs while there are too few.</summary>
    public static string Describe(SleepPattern pattern, string name, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(formats);
        return pattern is { UsualLongestStart: { } start, UsualLongest: { } length }
            ? Strings.Format("Pattern.Usual", name, formats.Time(start), Formats.Duration(length))
            : Strings.Format("Pattern.Empty", GetSleepPattern.DaysToSummarise);
    }
}
