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

/// <summary>Twins and siblings, side by side: one measure ("Sleep"), and each baby's value in the order of <c>CompareNames</c>.</summary>
public sealed record CompareRow(string KindCode, string Name, IReadOnlyList<string> Values);

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

    /// <summary>Twins and siblings: every born baby of the family, the shown one first; empty with one baby.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _compareNames = [];

    [ObservableProperty]
    private IReadOnlyList<CompareRow> _compare = [];

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var babies = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).ToList();
        if (babies.Count == 0)
            return;
        var baby = babies[0];

        _unit = household.VolumeUnit;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        bool IsShown(string kind) => KindVisibility.IsShown(household, kind);
        var result = await averages.ExecuteAsync(household.Id, baby.Id, Days, zone, time.GetLocalNow(), CancellationToken.None);
        Show(result, IsShown);

        // Side by side: the same average day for each born baby of the family.
        var born = babies.Where(b => b.IsBorn).ToList();
        if (born.Count < 2 || !baby.IsBorn)
        {
            (CompareNames, Compare) = ([], []);
            return;
        }

        var each = new List<IReadOnlyList<AverageRow>>();
        foreach (var b in born)
            each.Add(RowsFor(b.Id == baby.Id ? result : await averages.ExecuteAsync(household.Id, b.Id, Days, zone, time.GetLocalNow(), CancellationToken.None), IsShown));
        (CompareNames, Compare) = ([.. born.Select(b => b.Name)], SideBySide(each));
    }

    /// <summary>Lines up each baby's rows by measure; a measure one of them has no entries for shows "–".</summary>
    public static IReadOnlyList<CompareRow> SideBySide(IReadOnlyList<IReadOnlyList<AverageRow>> each)
    {
        ArgumentNullException.ThrowIfNull(each);
        var measures = each.SelectMany(rows => rows.Select(r => (r.KindCode, r.Name))).Distinct().ToList();
        return [.. measures.Select(m => new CompareRow(
            m.KindCode, m.Name, [.. each.Select(rows => rows.FirstOrDefault(r => r.Name == m.Name)?.Value ?? "–")]))];
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
        Rows = RowsFor(a, isShown);
    }

    private List<AverageRow> RowsFor(DailyAverages a, Func<string, bool> isShown)
    {
        if (!a.HasData)
            return [];
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

        return rows;
    }

    private string Volume(decimal millilitres) => _unit == VolumeUnit.Ounces
        ? $"{Math.Round(millilitres / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce, 1).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Oz")}"
        : $"{Math.Round(millilitres).ToString("0", Strings.Culture)} {Strings.Get("Unit.Ml")}";

    private static string Count(double value) => Math.Round(value, 1).ToString("0.#", Strings.Culture);
}
