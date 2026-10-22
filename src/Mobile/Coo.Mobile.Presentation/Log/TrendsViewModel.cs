using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Log;

/// <summary>What the trends chart can show, one at a time.</summary>
public enum TrendMetric
{
    Sleep,
    LongestSleep,
    Feeds,
    BreastFeed,
    Bottle,
    Diapers,
    Pumped,
}

/// <summary>One bar: the bucket's daily average of the chosen metric, in the metric's chart unit.</summary>
public sealed record TrendBar(string Label, double Value, bool HasData, bool IsCurrent, bool IsPartial, bool IsSelected);

/// <summary>One line of the comparison: the selected bucket against the one before it.</summary>
public sealed record TrendRow(TrendMetric Metric, string KindCode, string Name, string Value, string Detail, string Change, bool IsSelected);

/// <summary>
/// The Log's statistics: daily averages per day, week or month as a bar chart of one metric, and the selected bar
/// compared with the one before it, metric by metric ("13 h 05 m a day, +1 h 10 m vs the week before").
/// </summary>
public sealed partial class TrendsViewModel(IHouseholdStore households, GetCareTrends trends, TimeProvider time) : ObservableObject
{
    /// <summary>How many bars a page shows per grain.</summary>
    public static int BarsFor(TrendGrain grain) => grain switch
    {
        TrendGrain.Week => 8,
        TrendGrain.Month => 6,
        _ => 14,
    };

    /// <summary>Weeks start on Monday in every language: neutral English would start them on Sunday (ISO 8601, as the app's EU users expect).</summary>
    public const DayOfWeek FirstDayOfWeek = DayOfWeek.Monday;

    private IReadOnlyList<TrendBucket> _buckets = [];
    private VolumeUnit _unit = VolumeUnit.Millilitres;
    private Func<string, bool> _isShown = _ => true;
    private DateOnly _today;

    /// <summary>A date inside the page's last bar; null follows today.</summary>
    private DateOnly? _last;

    [ObservableProperty]
    private TrendGrain _grain = TrendGrain.Week;

    [ObservableProperty]
    private TrendMetric _metric = TrendMetric.Sleep;

    [ObservableProperty]
    private int _selected = -1;

    [ObservableProperty]
    private IReadOnlyList<TrendBar> _bars = [];

    /// <summary>The chart's dashed line: the average of the bars that have data, or null when there are none.</summary>
    [ObservableProperty]
    private double? _average;

    [ObservableProperty]
    private string _averageText = "";

    [ObservableProperty]
    private string _metricName = "";

    [ObservableProperty]
    private string _kindCode = "sleep";

    [ObservableProperty]
    private string _periodTitle = "";

    [ObservableProperty]
    private string _periodCaption = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _change = "";

    [ObservableProperty]
    private IReadOnlyList<TrendRow> _rows = [];

    [ObservableProperty]
    private bool _canGoNext;

    /// <summary>The metrics whose kind is shown, in order (Spec 009 §3.2: hidden kinds have no totals).</summary>
    public IReadOnlyList<TrendMetric> Metrics => [.. Enum.GetValues<TrendMetric>().Where(m => _isShown(KindOf(m)))];

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        var baby = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        if (baby is null)
            return;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetLocalNow();
        var today = DayMath.DateOf(now, zone);
        _today = today;
        _unit = household.VolumeUnit;
        _isShown = kind => KindVisibility.IsShown(household, kind);
        if (!_isShown(KindOf(Metric)))
            Metric = Metrics.Count > 0 ? Metrics[0] : TrendMetric.Sleep;

        var last = _last ?? today;
        var count = BarsFor(Grain);
        // One bar more than shown, so the first bar has one before it to compare with.
        _buckets = await trends.ExecuteAsync(
            household.Id, baby.Id, Grain, last, count + 1, FirstDayOfWeek, zone, now, CancellationToken.None);
        CanGoNext = _buckets[^1].End < today;
        if (Selected < 0 || Selected >= count)
            Selected = LastWithData(count);
        Show();
    }

    [RelayCommand]
    private Task SetGrain(TrendGrain grain)
    {
        if (Grain == grain)
            return Task.CompletedTask;
        Grain = grain;
        _last = null;
        Selected = -1;
        return RefreshAsync();
    }

    [RelayCommand]
    private void SetMetric(TrendMetric metric)
    {
        Metric = metric;
        Show();
    }

    [RelayCommand]
    private void Select(int index)
    {
        if (index < 0 || index >= Bars.Count || index == Selected)
            return;
        Selected = index;
        Show();
    }

    [RelayCommand]
    private Task Previous()
    {
        _last = GetCareTrends.Shift(_last ?? _today, Grain, -BarsFor(Grain));
        Selected = BarsFor(Grain) - 1;
        return RefreshAsync();
    }

    [RelayCommand]
    private Task Next()
    {
        if (!CanGoNext)
            return Task.CompletedTask;
        var last = GetCareTrends.Shift(_last ?? _today, Grain, BarsFor(Grain));
        _last = last >= _today ? null : last;
        Selected = -1;
        return RefreshAsync();
    }

    /// <summary>The metric's value in its chart unit: hours, minutes, a count, or the household's volume unit.</summary>
    public double ChartValue(TrendBucket bucket, TrendMetric metric)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return metric switch
        {
            TrendMetric.Sleep => bucket.Sleep.TotalHours,
            TrendMetric.LongestSleep => bucket.LongestSleep.TotalHours,
            TrendMetric.Feeds => bucket.Feeds,
            TrendMetric.BreastFeed => bucket.AverageBreastFeed.TotalMinutes,
            TrendMetric.Bottle => (double)Volume(bucket.BottleMl),
            TrendMetric.Diapers => bucket.Diapers,
            _ => (double)Volume(bucket.PumpedMl),
        };
    }

    public static string KindOf(TrendMetric metric) => metric switch
    {
        TrendMetric.Sleep or TrendMetric.LongestSleep => "sleep",
        TrendMetric.Feeds or TrendMetric.BreastFeed or TrendMetric.Bottle => "feeding",
        TrendMetric.Diapers => "diaper",
        _ => "pump",
    };

    public static string NameOf(TrendMetric metric) => Strings.Get("Trend." + metric);

    /// <summary>The metric's short name, for its chip.</summary>
    public static string ChipOf(TrendMetric metric) => Strings.Get("Trend.Chip." + metric);

    private void Show()
    {
        var shown = _buckets.Skip(1).ToList();
        if (shown.Count == 0 || Selected < 0)
            return;

        var bars = shown.Select((b, i) => new TrendBar(
                BarLabel(b),
                b.HasData ? ChartValue(b, Metric) : 0,
                b.HasData,
                b.IsCurrent,
                b.IsPartial,
                i == Selected))
            .ToList();
        Bars = bars;
        var withData = bars.Where(b => b.HasData).ToList();
        Average = withData.Count > 1 ? withData.Average(b => b.Value) : null;
        AverageText = Average is { } avg ? Strings.Format("Trend.Average", Text(Metric, avg)) : "";
        MetricName = NameOf(Metric);
        KindCode = KindOf(Metric);

        var current = _buckets[Selected + 1];
        var before = _buckets[Selected];
        PeriodTitle = Title(current, _today);
        PeriodCaption = Caption(current);
        Value = current.HasData ? Text(Metric, ChartValue(current, Metric)) : Strings.Get("Trend.NothingLogged");
        Change = ChangeText(Metric, current, before);
        Rows = [.. Metrics.Select(m => new TrendRow(
            m,
            KindOf(m),
            NameOf(m),
            current.HasData ? Text(m, ChartValue(current, m)) : "–",
            m == TrendMetric.Diapers && current.HasData
                ? Strings.Format("Trend.WetDirty", Count(current.Wet), Count(current.Dirty))
                : "",
            ChangeText(m, current, before),
            m == Metric))];
    }

    /// <summary>
    /// The bar selected first: the latest with data that is over. Today so far against all of yesterday read as a drop
    /// every morning ("-6 h 57 m vs the day before" at 2 PM); it stays one tap away.
    /// </summary>
    private int LastWithData(int count)
    {
        int? partial = null;
        for (var i = count - 1; i >= 0; i--)
        {
            if (i + 1 >= _buckets.Count || !_buckets[i + 1].HasData)
                continue;
            if (!_buckets[i + 1].IsPartial)
                return i;
            partial ??= i;
        }

        return partial ?? count - 1;
    }

    private string ChangeText(TrendMetric metric, TrendBucket current, TrendBucket before)
    {
        if (!current.HasData)
            return "";
        if (!before.HasData)
            return Strings.Get("Trend.NothingBefore");

        var vs = Strings.Get(Grain switch
        {
            TrendGrain.Week => "Trend.VsWeek",
            TrendGrain.Month => "Trend.VsMonth",
            _ => "Trend.VsDay",
        });
        var delta = ChartValue(current, metric) - ChartValue(before, metric);
        if (Text(metric, Math.Abs(delta)) == Text(metric, 0))
            return $"{Strings.Get("Trend.NoChange")} {vs}";
        // ASCII signs: Figtree has no U+2212 (the chevron lesson from Increment 6).
        return $"{(delta > 0 ? "+" : "-")}{Text(metric, Math.Abs(delta))} {vs}";
    }

    /// <summary>A value in the metric's chart unit as the UI reads it: "13 h 05 m", "8.5", "120 ml".</summary>
    public string Text(TrendMetric metric, double value) => metric switch
    {
        TrendMetric.Sleep or TrendMetric.LongestSleep => Formats.Duration(TimeSpan.FromHours(value)),
        TrendMetric.BreastFeed => Formats.Duration(TimeSpan.FromMinutes(value)),
        TrendMetric.Feeds or TrendMetric.Diapers => Count(value),
        _ => _unit == VolumeUnit.Ounces
            ? $"{Math.Round(value, 1).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Oz")}"
            : $"{Math.Round(value).ToString("0", Strings.Culture)} {Strings.Get("Unit.Ml")}",
    };

    private static string Count(double value) => Math.Round(value, 1).ToString("0.#", Strings.Culture);

    private decimal Volume(decimal millilitres) =>
        _unit == VolumeUnit.Ounces ? millilitres / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce : millilitres;

    private string BarLabel(TrendBucket bucket) => Grain switch
    {
        TrendGrain.Week => bucket.Start.ToString("d MMM", Strings.Culture),
        TrendGrain.Month => bucket.Start.ToString("MMM", Strings.Culture),
        _ => bucket.Start.ToString("ddd", Strings.Culture)[..Math.Min(2, bucket.Start.ToString("ddd", Strings.Culture).Length)] + "\n" + bucket.Start.Day.ToString(CultureInfo.InvariantCulture),
    };

    private string Title(TrendBucket bucket, DateOnly today) => Grain switch
    {
        TrendGrain.Week => Strings.Format(
            "Trend.WeekRange", bucket.Start.ToString("d MMM", Strings.Culture), bucket.End.ToString("d MMM", Strings.Culture)),
        TrendGrain.Month => bucket.Start.ToString("MMMM yyyy", Strings.Culture),
        _ => bucket.Start == today ? Strings.Get("Section.Today") : bucket.Start.ToString("dddd, d MMMM", Strings.Culture),
    };

    private string Caption(TrendBucket bucket)
    {
        if (!bucket.HasData)
            return "";
        if (Grain == TrendGrain.Day)
            return bucket.IsPartial ? Strings.Get("Trend.SoFar") : "";
        var days = Strings.Plural("Trend.DaysCounted", bucket.DaysCounted);
        return bucket.IsCurrent || bucket.IsPartial ? $"{days} · {Strings.Get("Trend.SoFar")}" : days;
    }
}
