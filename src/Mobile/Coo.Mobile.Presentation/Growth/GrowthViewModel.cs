using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.Households;
using Coo.Mobile.Application.Growth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Growth;

/// <summary>One WHO percentile line, sampled across the chart's ages: (age in months, value).</summary>
public sealed record GrowthCurve(int Percentile, IReadOnlyList<(double Age, double Value)> Points);

/// <summary>A past measurement as listed under the chart: "12 Oct", "5.12 kg", "48th percentile".</summary>
public sealed record GrowthRow(Coo.Domain.Common.EntryId Id, string Date, string Value, string Percentile);

/// <summary>
/// The growth chart: weight, length or head circumference against the WHO percentile lines for the first two years,
/// in the household's units for the text (the chart itself is in kg and cm, as WHO draws it).
/// </summary>
public sealed partial class GrowthViewModel(
    IHouseholdStore households, GetGrowth getGrowth, IGrowthChartPreference preference, IClockFormat clock, TimeProvider time) : ObservableObject
{
    private IReadOnlyList<(CareEntry Entry, GrowthDetails Details)> _entries = [];
    private DateOnly? _birth;
    private Formats _formats = new(true, VolumeUnit.Millilitres);

    [ObservableProperty]
    private GrowthMeasure _measure = GrowthMeasure.Weight;

    [ObservableProperty]
    private ChartSex? _sex;

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private IReadOnlyList<GrowthPoint> _points = [];

    [ObservableProperty]
    private IReadOnlyList<GrowthCurve> _curves = [];

    [ObservableProperty]
    private double _maxAge = 3;

    [ObservableProperty]
    private string _latest = "";

    [ObservableProperty]
    private IReadOnlyList<GrowthRow> _rows = [];

    /// <summary>False before the birth is recorded: no age, so no chart.</summary>
    public bool HasBirth => _birth is not null;

    /// <summary>The y axis's unit, as WHO draws it.</summary>
    public string AxisUnit => Measure == GrowthMeasure.Weight ? "kg" : "cm";

    public async Task LoadAsync()
    {
        Sex = await preference.GetAsync(CancellationToken.None);
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null : (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        (BabyName, _birth) = (baby?.Name ?? "", baby?.BirthDate);
        _formats = new Formats(clock.Is24Hour, household?.VolumeUnit ?? VolumeUnit.Millilitres);
        _entries = household is null || baby?.BirthDate is not { } birth
            ? []
            : await getGrowth.ExecuteAsync(household.Id, baby.Id, birth, time.GetUtcNow(), CancellationToken.None);
        Rebuild();
    }

    [RelayCommand]
    private void SetMeasure(GrowthMeasure measure)
    {
        Measure = measure;
        Rebuild();
    }

    [RelayCommand]
    private async Task SetSex(ChartSex sex)
    {
        await preference.SetAsync(sex, CancellationToken.None);
        Sex = sex;
        Rebuild();
    }

    private void Rebuild()
    {
        OnPropertyChanged(nameof(HasBirth));
        OnPropertyChanged(nameof(AxisUnit));
        if (_birth is not { } birth || Sex is not { } sex)
        {
            (Points, Curves, Rows, Latest) = ([], [], [], "");
            return;
        }

        Points = GetGrowth.Points(_entries, Measure, sex, birth);
        var age = WhoGrowth.AgeMonths(birth, time.GetUtcNow());
        // The first months in detail, then the chart widens with the baby: one month past today, two years at most.
        MaxAge = Math.Clamp(Math.Ceiling(Math.Max(age, Points.Count > 0 ? Points.Max(p => p.AgeMonths) : 0)) + 1, 3, WhoGrowth.MaxMonths);
        Curves = [.. WhoGrowth.Lines.Select(line => new GrowthCurve(line.Percentile, [.. Enumerable.Range(0, (int)(MaxAge * 4) + 1)
            .Select(i => i / 4.0)
            .Select(a => (a, WhoGrowth.ValueAt(Measure, sex, a, line.Z) ?? 0))]))];

        var zone = time.LocalTimeZone;
        Rows = [.. _entries
            .Where(x => WhoGrowth.ValueOf(x.Details, Measure) is not null)
            .Reverse()
            .Select(x =>
            {
                var at = x.Entry.Timing.OccurredAt;
                var value = WhoGrowth.ValueOf(x.Details, Measure)!.Value;
                var percentile = WhoGrowth.Percentile(Measure, sex, WhoGrowth.AgeMonths(birth, at), value);
                return new GrowthRow(x.Entry.Id, TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM yyyy", Strings.Culture), Shown(x.Details), PercentileText(percentile));
            })];
        Latest = Rows.Count == 0 ? "" : $"{Rows[0].Value} · {Rows[0].Percentile}".TrimEnd(' ', '·');
    }

    private string Shown(GrowthDetails details) => Measure switch
    {
        GrowthMeasure.Weight => _formats.Weight(details.WeightGrams!.Value),
        GrowthMeasure.Length => _formats.Length(details.LengthMm!.Value),
        _ => _formats.Length(details.HeadMm!.Value),
    };

    /// <summary>"48th percentile", rounded and kept within 1–99: a chart is not precise to the extremes.</summary>
    public static string PercentileText(double? percentile)
    {
        if (percentile is not { } p)
            return "";
        var n = (int)Math.Clamp(Math.Round(p), 1, 99);
        return Strings.Format("Growth.Percentile", Strings.Language == "en" ? n + Suffix(n) : n.ToString(Strings.Culture));
    }

    private static string Suffix(int n) => (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}
