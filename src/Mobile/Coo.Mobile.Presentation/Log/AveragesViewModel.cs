using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Log;

/// <summary>One line of "On an average day": the kind's colour, what, how much, and the detail under it.</summary>
public sealed record AverageRow(string KindCode, string Name, string Value, string Detail);

/// <summary>
/// The top of Stats: an average day over the last 7, 14 or 30 days, written as a doctor asks ("How much does she drink
/// a day?"): milk from bottles a day and per bottle, feeds, sleep, diapers. Before this, the answer needed the right chip,
/// the right grain and a bar (owner, at the doctor's, 2026-11-02).
/// </summary>
public sealed partial class AveragesViewModel(IHouseholdStore households, GetDailyAverages averages, TimeProvider time) : ObservableObject
{
    public static readonly IReadOnlyList<int> Periods = [7, 14, 30];

    private VolumeUnit _unit = VolumeUnit.Millilitres;

    [ObservableProperty]
    private int _days = 7;

    [ObservableProperty]
    private IReadOnlyList<AverageRow> _rows = [];

    /// <summary>"Last 7 days · 6 days logged", or that nothing was logged.</summary>
    [ObservableProperty]
    private string _caption = "";

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var baby = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        if (baby is null)
            return;

        _unit = household.VolumeUnit;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var result = await averages.ExecuteAsync(household.Id, baby.Id, Days, zone, time.GetLocalNow(), CancellationToken.None);
        Show(result, kind => KindVisibility.IsShown(household, kind));
    }

    [RelayCommand]
    private Task SetDays(int days)
    {
        if (Days == days)
            return Task.CompletedTask;
        Days = days;
        return RefreshAsync();
    }

    /// <summary>The rows for <paramref name="a"/>; a kind that is hidden, or was never logged in the period, has none.</summary>
    public void Show(DailyAverages a, Func<string, bool> isShown)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(isShown);
        if (!a.HasData)
        {
            Caption = Strings.Format("Averages.Nothing", Days);
            Rows = [];
            return;
        }

        var logged = Strings.Plural("Trend.DaysCounted", a.DaysCounted);
        Caption = a.IncludesToday
            ? $"{Strings.Get("Section.Today")} · {Strings.Get("Trend.SoFar")}"
            : $"{Strings.Format("Averages.LastDays", Days)} · {logged}";

        var rows = new List<AverageRow>();
        if (isShown("feeding") && a.Bottles > 0)
        {
            var each = a.MlPerBottle is { } perBottle ? Strings.Format("Averages.Each", Volume(perBottle)) : "";
            rows.Add(new AverageRow("feeding", Strings.Get("Averages.Milk"), Volume(a.BottleMl), Strings.Format("Averages.Bottles", Count(a.Bottles), each)));
        }

        if (isShown("feeding") && a.Feeds > 0)
        {
            var detail = a.BreastFeeds > 0
                ? Strings.Format("Averages.Breastfeeds", Count(a.BreastFeeds), Formats.Duration(a.AverageBreastFeed))
                : "";
            rows.Add(new AverageRow("feeding", Strings.Get("Averages.Feeds"), Count(a.Feeds), detail));
        }

        if (isShown("sleep") && a.Sleep > TimeSpan.Zero)
            rows.Add(new AverageRow("sleep", Strings.Get("Averages.Sleep"), Formats.Duration(a.Sleep), Strings.Format("Averages.Longest", Formats.Duration(a.LongestSleep))));

        if (isShown("diaper") && a.Diapers > 0)
            rows.Add(new AverageRow("diaper", Strings.Get("Averages.Diapers"), Count(a.Diapers), Strings.Format("Trend.WetDirty", Count(a.Wet), Count(a.Dirty))));

        if (isShown("pump") && a.PumpedMl > 0)
            rows.Add(new AverageRow("pump", Strings.Get("Averages.Pumped"), Volume(a.PumpedMl), ""));

        Rows = rows;
    }

    private string Volume(decimal millilitres) => _unit == VolumeUnit.Ounces
        ? $"{Math.Round(millilitres / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce, 1).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Oz")}"
        : $"{Math.Round(millilitres).ToString("0", Strings.Culture)} {Strings.Get("Unit.Ml")}";

    private static string Count(double value) => Math.Round(value, 1).ToString("0.#", Strings.Culture);
}
