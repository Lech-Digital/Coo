using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Growth;

public enum GrowthMeasure
{
    Weight,
    Length,
    Head,
}

/// <summary>Which WHO standard a chart compares with. Chosen on the growth page and kept on this phone only.</summary>
public enum ChartSex
{
    Girl,
    Boy,
}

/// <summary>One measurement on the chart: when, the baby's age in months, the value (kg or cm) and its WHO percentile.</summary>
public sealed record GrowthPoint(DateTimeOffset At, double AgeMonths, double Value, double? Percentile);

/// <summary>
/// The WHO Child Growth Standards, 0–24 months, by the LMS method: a measurement's z-score is
/// ((x/M)^L − 1) / (L·S), and the value at a z-score is M·(1 + L·S·z)^(1/L) (M·e^(S·z) when L is 0). Between whole
/// months the L, M and S values are interpolated. Ages past 24 months have no percentile here.
/// </summary>
public static class WhoGrowth
{
    public const double DaysPerMonth = 30.4375;
    public const double MaxMonths = 24;

    /// <summary>The percentile lines WHO draws on its charts: 3rd, 15th, 50th, 85th and 97th.</summary>
    public static IReadOnlyList<(int Percentile, double Z)> Lines { get; } =
        [(3, -1.880794), (15, -1.036433), (50, 0), (85, 1.036433), (97, 1.880794)];

    public static double AgeMonths(DateOnly birth, DateTimeOffset at) =>
        (DateOnly.FromDateTime(at.DateTime).DayNumber - birth.DayNumber) / DaysPerMonth;

    /// <summary>The value at <paramref name="z"/> for the age; null outside 0–24 months.</summary>
    public static double? ValueAt(GrowthMeasure measure, ChartSex sex, double ageMonths, double z)
    {
        if (Lms(measure, sex, ageMonths) is not var (l, m, s))
            return null;
        return Math.Abs(l) < 1e-9 ? m * Math.Exp(s * z) : m * Math.Pow(1 + (l * s * z), 1 / l);
    }

    /// <summary>The WHO percentile (0–100) of a value at an age; null outside 0–24 months.</summary>
    public static double? Percentile(GrowthMeasure measure, ChartSex sex, double ageMonths, double value)
    {
        if (value <= 0 || Lms(measure, sex, ageMonths) is not var (l, m, s))
            return null;
        var z = Math.Abs(l) < 1e-9 ? Math.Log(value / m) / s : (Math.Pow(value / m, l) - 1) / (l * s);
        return NormalCdf(z) * 100;
    }

    /// <summary>A measurement's value in the chart's unit: kg for weight, cm for length and head; null when not measured.</summary>
    public static double? ValueOf(GrowthDetails details, GrowthMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(details);
        return measure switch
        {
            GrowthMeasure.Weight => details.WeightGrams / 1000.0,
            GrowthMeasure.Length => details.LengthMm / 10.0,
            _ => details.HeadMm / 10.0,
        };
    }

    private static (double L, double M, double S)? Lms(GrowthMeasure measure, ChartSex sex, double ageMonths)
    {
        if (ageMonths < 0 || ageMonths > MaxMonths)
            return null;
        var table = (measure, sex) switch
        {
            (GrowthMeasure.Weight, ChartSex.Girl) => WhoTables.WeightGirl,
            (GrowthMeasure.Weight, ChartSex.Boy) => WhoTables.WeightBoy,
            (GrowthMeasure.Length, ChartSex.Girl) => WhoTables.LengthGirl,
            (GrowthMeasure.Length, ChartSex.Boy) => WhoTables.LengthBoy,
            (GrowthMeasure.Head, ChartSex.Girl) => WhoTables.HeadGirl,
            _ => WhoTables.HeadBoy,
        };
        var low = (int)Math.Floor(ageMonths);
        if (low >= table.Length - 1)
            return table[^1];
        var t = ageMonths - low;
        var (a, b) = (table[low], table[low + 1]);
        return (a.L + ((b.L - a.L) * t), a.M + ((b.M - a.M) * t), a.S + ((b.S - a.S) * t));
    }

    /// <summary>Φ(z), from erf by Abramowitz and Stegun 7.1.26 (error below 1.5·10⁻⁷).</summary>
    internal static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + (0.3275911 * x));
        var erf = 1 - ((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}

/// <summary>The growth chart's preference on this phone: which WHO standard to compare with; null until chosen.</summary>
public interface IGrowthChartPreference
{
    Task<ChartSex?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(ChartSex sex, CancellationToken cancellationToken);
}

/// <summary>Every growth measurement of a baby since birth, oldest first, with its percentile for the chosen standard.</summary>
public sealed class GetGrowth(ICareLogStore store)
{
    public async Task<IReadOnlyList<(CareEntry Entry, GrowthDetails Details)>> ExecuteAsync(
        HouseholdId household, BabyId baby, DateOnly birth, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(birth.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var stored = await store.ListAsync(household, from, now.AddDays(1), cancellationToken);
        return [.. stored.OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => e.Details is GrowthDetails && e.Subject is BabySubject { BabyId: var id } && id == baby)
            .OrderBy(e => e.Timing.OccurredAt)
            .Select(e => (e, (GrowthDetails)e.Details))];
    }

    /// <summary>The chart's points for one measure: the entries that measured it, with age and percentile.</summary>
    public static IReadOnlyList<GrowthPoint> Points(
        IEnumerable<(CareEntry Entry, GrowthDetails Details)> entries, GrowthMeasure measure, ChartSex sex, DateOnly birth) =>
        [.. entries
            .Select(x => (x.Entry.Timing.OccurredAt, Value: WhoGrowth.ValueOf(x.Details, measure)))
            .Where(x => x.Value is not null)
            .Select(x =>
            {
                var age = WhoGrowth.AgeMonths(birth, x.OccurredAt);
                return new GrowthPoint(x.OccurredAt, age, x.Value!.Value, WhoGrowth.Percentile(measure, sex, age, x.Value.Value));
            })];
}
