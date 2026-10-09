using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Log;

public sealed record TotalItem(string KindCode, string Number, string Unit);

/// <summary>The Log screen (Spec 006 §7): one local day, with ribbon, totals and timeline.</summary>
public sealed partial class LogViewModel(
    IHouseholdStore households, GetDay getDay, IClockFormat clock, UndoService undo, ThemeController theme, TimeProvider time, WhoLabelsSource whoSource) : ObservableObject
{
    [ObservableProperty]
    private DateOnly _date = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private IReadOnlyList<ChartItem> _chartItems = [];

    [ObservableProperty]
    private IReadOnlyList<ChartBand> _quietBands = [];

    [ObservableProperty]
    private IReadOnlyList<HourTick> _hours = [];

    /// <summary>Where now falls on today's chart, rounded to the minute so a quick return to Log changes nothing; null on other days.</summary>
    [ObservableProperty]
    private double? _nowAt;

    [ObservableProperty]
    private string _chartSummary = "";

    private (LocalDay Day, bool IsToday, Formats Formats, TimeZoneInfo Zone)? _shown;

    /// <summary>The zoomed day's title for the part in view (Spec 009b §3).</summary>
    public string RangeTitle(double from, double to) =>
        _shown is { } s ? DayChart.RangeTitle(s.Day, s.IsToday, from, to, s.Formats, s.Zone) : "";

    [ObservableProperty]
    private IReadOnlyList<TotalItem> _totals = [];

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _rows = [];

    [ObservableProperty]
    private bool _canGoNext;

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var babies = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).ToList();
        if (babies.Count == 0)
            return;
        var baby = babies[0];

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        // To the minute: a running sleep or feed ends at now, so a return to Log within the minute changes nothing and the
        // page does not redraw (reviewer's finding, after the owner's Pixel 10 finding).
        var now = time.GetLocalNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute));
        var today = DateOnly.FromDateTime(now.DateTime);

        var day = await getDay.ExecuteAsync(household.Id, baby.Id, Date, zone, now, CancellationToken.None);
        var formats = new Formats(clock.Is24Hour, household.VolumeUnit);

        Title = Date == today ? Strings.Get("Section.Today") : Date.ToString("dddd d MMMM", Strings.Culture);
        CanGoNext = Date < today;
        bool IsShown(string kind) => KindVisibility.IsShown(household, kind);
        var isToday = Date == today;
        _shown = (day.Day, isToday, formats, zone);
        var items = DayChart.Build(day, formats, zone, IsShown);
        ChartItems = Same(ChartItems, items);
        QuietBands = Same(QuietBands, DayChart.QuietBands(theme.Settings.QuietHours, day.Day, zone));
        Hours = Same(Hours, DayChart.Hours(day.Day, zone, formats));
        NowAt = isToday ? Math.Round(day.Day.Fraction(now) * day.Day.Length.TotalMinutes) / day.Day.Length.TotalMinutes : null;
        ChartSummary = DayChart.Summary(items, day.Day, IsShown);
        var pumped = household.VolumeUnit == VolumeUnit.Ounces
            ? (Math.Round(day.Totals.PumpedMl / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce, 1).ToString("0.#", Strings.Culture), Strings.Get("Totals.PumpedOz"))
            : (Math.Round(day.Totals.PumpedMl).ToString("0", Strings.Culture), Strings.Get("Totals.PumpedMl"));
        // Hidden kinds have no totals; their entries stay in the timeline (Spec 009 §3.2).
        TotalItem[] totals =
        [
            new("feeding", day.Totals.Feeds.ToString(Strings.Culture), PluralWord("Totals.Feeds", day.Totals.Feeds)),
            new("diaper", day.Totals.Diapers.ToString(Strings.Culture), PluralWord("Totals.Diapers", day.Totals.Diapers)),
            new("sleep", Formats.Duration(day.Totals.Sleep), Strings.Get("Totals.Sleep")),
            new("pump", pumped.Item1, pumped.Item2),
        ];
        Totals = Same(Totals, [.. totals.Where(t => KindVisibility.IsShown(household, t.KindCode))]);
        var who = await whoSource.ForAsync(household.Id);
        Rows = Same(Rows, [.. day.Entries.Where(e => !undo.IsPendingDelete(e.Id)).Select(e => new TimelineItem(e.Id, formats.Time(e.Start), e.KindCode, formats.Title(e, now), who.For(e)))]);
    }

    [RelayCommand]
    private Task Previous()
    {
        Date = Date.AddDays(-1);
        return RefreshAsync();
    }

    [RelayCommand]
    private Task Next()
    {
        if (!CanGoNext)
            return Task.CompletedTask;
        Date = Date.AddDays(1);
        return RefreshAsync();
    }

    /// <summary>Resets viewing date to current local day if needed (e.g. overnight transition).</summary>
    public void ResetToToday()
    {
        Date = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
    }

    /// <summary>
    /// The current list when the new one shows the same thing, so a refresh with nothing new raises no change and the
    /// page does not redraw (owner's finding on the Pixel 10: every tab visit redrew Log).
    /// </summary>
    private static IReadOnlyList<T> Same<T>(IReadOnlyList<T> current, IReadOnlyList<T> next) =>
        current.SequenceEqual(next) ? current : next;

    private static string PluralWord(string key, int n) =>
        Strings.Get($"{key}.{PluralRules.Select(Strings.Language, n)}");
}
