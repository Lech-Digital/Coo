namespace Coo.Domain.Insights;

/// <summary>
/// Every constant of the predictor in one place (ADR 0008 §4). The backtest tunes them, and any change bumps
/// <see cref="SleepPredictor.Version"/>. Tuned from the ADR's starting values (2026-10-23): a prior weight of 5 and a
/// half-life of 4 days could never reach high confidence (a fortnight of daily windows weighs about 4.6), and half the
/// interquartile range missed the 60% hit rate.
/// </summary>
public sealed record PredictorParameters(
    TimeSpan LookBack,
    double HalfLifeDays,
    double PriorWeight,
    double ShortNapFactor,
    TimeSpan ShortNap,
    double BandFactor,
    TimeSpan BandMin,
    TimeSpan BandMax,
    double HighConfidenceWeight,
    TimeSpan HighConfidenceSpread,
    TimeSpan LostTrackAfter)
{
    public static PredictorParameters Default { get; } = new(
        LookBack: TimeSpan.FromDays(14),
        HalfLifeDays: 7,
        PriorWeight: 2,
        ShortNapFactor: 0.85,
        ShortNap: TimeSpan.FromMinutes(45),
        BandFactor: 1,
        BandMin: TimeSpan.FromMinutes(10),
        BandMax: TimeSpan.FromMinutes(30),
        HighConfidenceWeight: 3,
        HighConfidenceSpread: TimeSpan.FromMinutes(30),
        LostTrackAfter: TimeSpan.FromHours(8));
}

public enum PredictionConfidence
{
    Low,
    High,
}

/// <summary>
/// When the next sleep is likely to start (ADR 0008 §4), and what explains it.
/// </summary>
/// <param name="Position">Which wake window of the day this is: 1 is the morning's.</param>
/// <param name="IsBedtime">The next sleep is likely the night's rather than a nap.</param>
/// <param name="PersonalMedian">This baby's own typical window at this position; null without observations.</param>
/// <param name="DaysUsed">The days the observations came from.</param>
/// <param name="ShortNapAdjusted">The last nap was short, so the window was shortened.</param>
/// <param name="ShortNapFactor">How much a short nap shortens the next window: her own when learned, else the default.</param>
/// <param name="ShortNapLearned">The factor comes from her own windows after short naps.</param>
/// <param name="UsualBedtime">When her night sleep has usually started lately; null before three nights.</param>
/// <param name="BedtimeClockWeight">How much the bedtime leans on that clock time rather than on her wake window (0–0.6).</param>
public sealed record SleepPrediction(
    int Position,
    bool IsBedtime,
    DateTimeOffset AwakeSince,
    DateTimeOffset Earliest,
    DateTimeOffset Centre,
    DateTimeOffset Latest,
    PredictionConfidence Confidence,
    TimeSpan Estimate,
    TimeSpan? PersonalMedian,
    int DaysUsed,
    int Observations,
    WakeWindowBand Prior,
    bool ShortNapAdjusted,
    string Version,
    double ShortNapFactor = 0.85,
    bool ShortNapLearned = false,
    TimeOnly? UsualBedtime = null,
    double BedtimeClockWeight = 0)
{
    /// <summary>The fewest of the baby's own windows, on as many days, before the window is shown as hers.</summary>
    public const int MinimumPersonalWindows = 3;

    /// <summary>
    /// Enough of this baby's own windows (at least three, on three days) to show the window as hers. Confidence only
    /// sets how wide it is: a young baby's windows often vary by more than the High spread, and requiring High kept a
    /// three-week log on "log a few more days" (owner, 2026-11-03).
    /// </summary>
    public bool IsPersonal => PersonalMedian is not null && Observations >= MinimumPersonalWindows && DaysUsed >= MinimumPersonalWindows;
}

/// <summary>One observed wake window: from waking to the next sleep, at its position in the day.</summary>
/// <param name="Length">At its usual length: a window after a short nap is scaled back up by the default factor.</param>
/// <param name="Raw">As it happened.</param>
internal sealed record WakeWindowObservation(int Position, DateTimeOffset From, TimeSpan Length, bool EndedAtNight, TimeSpan Raw = default, bool AfterShortNap = false);

/// <summary>
/// The sleep predictor (ADR 0008): an age prior blended with the baby's own recent wake windows, a pure function of the
/// sleeps, the age and <c>now</c>. No prediction while the baby sleeps, nor at night: v1 does not predict when a sleep
/// ends, and nobody wants a nap suggested at 3 AM.
/// </summary>
public static class SleepPredictor
{
    public const string Version = "sleep-v3";

    /// <param name="sleeps">The baby's sleeps of at least the last <see cref="PredictorParameters.LookBack"/>, any order.</param>
    /// <param name="correctedAgeDays">Corrected age when the baby was born early (ADR 0008 §2).</param>
    public static SleepPrediction? Predict(
        IEnumerable<SleepSpan> sleeps, int correctedAgeDays, DateTimeOffset now, TimeZoneInfo zone, PredictorParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        var p = parameters ?? PredictorParameters.Default;
        var recent = sleeps.Where(s => s.Start <= now && s.Start >= now - p.LookBack - TimeSpan.FromDays(1)).OrderBy(s => s.Start).ToList();
        if (recent.Count == 0 || recent.Any(s => s.End is null || s.End > now))
            return null;

        var window = SleepClassifier.LearnNightWindow(recent, zone);
        var classes = SleepClassifier.Classify(recent, window, zone);
        var last = recent[^1];
        var awake = last.End!.Value;

        // Awake in the night (a night waking, or before the morning): no nap to suggest.
        if (window.IsNightTime(SleepClassifier.Local(awake, zone)) && classes[^1] == SleepClass.Night)
            return null;

        var (observations, position) = Walk(recent, classes, p);
        position ??= FallbackPosition(recent, classes, awake, zone);

        var prior = WakeWindowPrior.For(correctedAgeDays);
        var priorEstimate = prior.At(position.Value);

        // Her own short-nap effect (owner, 2026-11-06: the window depends on how long she slept just before): once she
        // has a few windows after short naps, how much shorter they were than her usual window at the same place in the
        // day; until then the default.
        var (factor, learned) = ShortNapEffect(observations.Where(o => o.From >= now - p.LookBack).ToList(), p);
        observations = [.. observations.Select(o => o.AfterShortNap ? o with { Length = o.Raw / factor } : o)];
        var usable = observations
            .Where(o => o.Position == position && o.From >= now - p.LookBack)
            .Where(o => o.Length >= prior.Low * 0.5 && o.Length <= prior.High * 2)
            .ToList();

        var weights = usable.Select(o => Math.Pow(0.5, (now - o.From).TotalDays / p.HalfLifeDays)).ToList();
        var nEff = weights.Sum();
        TimeSpan? personal = usable.Count > 0 ? WeightedMedian(usable.Select(o => o.Length).ToList(), weights) : null;
        var estimate = personal is { } median
            ? TimeSpan.FromTicks((long)(((nEff * median.Ticks) + (p.PriorWeight * priorEstimate.Ticks)) / (nEff + p.PriorWeight)))
            : priorEstimate;

        var shortNap = classes[^1] == SleepClass.Nap && last.Length < p.ShortNap;
        if (shortNap)
            estimate *= factor;

        var spread = usable.Count >= 2 ? InterquartileRange([.. usable.Select(o => o.Length)]) : (TimeSpan?)null;
        var band = spread is { } iqr ? Clamp(iqr * p.BandFactor, p.BandMin, p.BandMax) : p.BandMax;
        var confidence = nEff >= p.HighConfidenceWeight && spread <= p.HighConfidenceSpread ? PredictionConfidence.High : PredictionConfidence.Low;

        var bedtime = usable.Count > 0 ? usable.Count(o => o.EndedAtNight) * 2 > usable.Count : position > prior.TypicalNaps;
        var centre = awake + estimate;

        // Bedtime by the clock as well (owner, 2026-11-06: "babies get to the rhythm with growing up"): a day/night
        // rhythm emerges between about 3 and 6 months, and the night's start settles at a time of its own. So the more
        // regular her recent bedtimes and the older she is, the more the window leans towards her usual bedtime.
        var usualBedtime = UsualBedtime(recent, classes, now, zone, p);
        var clockWeight = 0.0;
        if (bedtime && usualBedtime is { } usual)
        {
            clockWeight = ClockWeight(correctedAgeDays, usual.Spread);
            var local = TimeZoneInfo.ConvertTime(awake, zone);
            var evening = new DateTimeOffset(local.Date.AddHours(12) + TimeSpan.FromMinutes(usual.FromNoon), local.Offset);
            centre += (evening - centre) * clockWeight;
        }
        var days = usable.Select(o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.From, zone).DateTime)).Distinct().Count();
        return new SleepPrediction(
            position.Value, bedtime, awake, centre - band, centre, centre + band, confidence, estimate, personal, days, usable.Count, prior,
            shortNap, Version, factor, learned, usualBedtime?.Time, clockWeight);
    }

    /// <summary>Nights needed before her usual bedtime counts.</summary>
    public const int BedtimesToLearn = 3;

    /// <summary>
    /// When her nights have started over the last fortnight: the recency-weighted median of each night's first sleep, in
    /// minutes from noon so a night across midnight stays one number, with the spread between them.
    /// </summary>
    internal static (TimeOnly Time, int FromNoon, TimeSpan Spread)? UsualBedtime(
        IReadOnlyList<SleepSpan> sleeps, IReadOnlyList<SleepClass> classes, DateTimeOffset now, TimeZoneInfo zone, PredictorParameters p)
    {
        var starts = new List<(int FromNoon, double Weight)>();
        for (var i = 0; i < sleeps.Count; i++)
        {
            if (classes[i] != SleepClass.Night || sleeps[i].Start < now - p.LookBack)
                continue;
            // A night's first sleep: after a nap, or after a gap too long to be a night waking.
            if (i > 0 && classes[i - 1] == SleepClass.Night && sleeps[i].Start - sleeps[i - 1].End!.Value <= SleepClassifier.NightWakingAtMost)
                continue;
            var local = SleepClassifier.Local(sleeps[i].Start, zone);
            var fromNoon = (((local.Hour * 60) + local.Minute) - (12 * 60) + (24 * 60)) % (24 * 60);
            starts.Add((fromNoon, Math.Pow(0.5, (now - sleeps[i].Start).TotalDays / p.HalfLifeDays)));
        }

        if (starts.Count < BedtimesToLearn)
            return null;
        var minutes = starts.Select(x => TimeSpan.FromMinutes(x.FromNoon)).ToList();
        var median = (int)WeightedMedian(minutes, [.. starts.Select(x => x.Weight)]).TotalMinutes;
        return (new TimeOnly(12, 0).AddMinutes(median), median, InterquartileRange(minutes));
    }

    /// <summary>
    /// 0 before 8 weeks, rising to 0.6 at 6 months (when most babies have a day/night rhythm); scaled down as her
    /// bedtimes spread: in full within 30 minutes of each other, not at all beyond 90.
    /// </summary>
    internal static double ClockWeight(int correctedAgeDays, TimeSpan spread)
    {
        var age = Math.Clamp((correctedAgeDays - 56) / (183.0 - 56), 0, 1) * 0.6;
        var regular = Math.Clamp(1 - ((spread.TotalMinutes - 30) / 60), 0, 1);
        return age * regular;
    }

    /// <summary>Windows after a short nap needed before her own effect replaces the default.</summary>
    public const int ShortNapWindowsToLearn = 3;

    /// <summary>
    /// Her windows after short naps against her usual window at the same position (the median of the windows after
    /// longer naps there), as one factor between 0.6 and 1; the default until there are enough to compare.
    /// </summary>
    internal static (double Factor, bool Learned) ShortNapEffect(IReadOnlyList<WakeWindowObservation> observations, PredictorParameters p)
    {
        var usual = observations
            .Where(o => !o.AfterShortNap)
            .GroupBy(o => o.Position)
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => Median([.. g.Select(o => o.Length.TotalMinutes)]));
        var ratios = observations
            .Where(o => o.AfterShortNap && o.Raw > TimeSpan.Zero && usual.ContainsKey(o.Position))
            .Select(o => o.Raw.TotalMinutes / usual[o.Position])
            .ToList();
        if (ratios.Count < ShortNapWindowsToLearn)
            return (p.ShortNapFactor, false);
        return (Math.Clamp(Median(ratios), 0.6, 1.0), true);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>
    /// The wake windows between the sleeps, each at its position: a night's end starts the day at 1, each nap moves it on.
    /// A night waking is not a window, and a gap too long to be one (a sleep not logged) loses the position until the next
    /// night. Returns the position of the window now running, null when it is not known.
    /// </summary>
    internal static (List<WakeWindowObservation> Observations, int? Position) Walk(
        IReadOnlyList<SleepSpan> sleeps, IReadOnlyList<SleepClass> classes, PredictorParameters p)
    {
        var observations = new List<WakeWindowObservation>();
        int? position = null;
        for (var i = 0; i < sleeps.Count; i++)
        {
            if (i > 0)
            {
                var gap = sleeps[i].Start - sleeps[i - 1].End!.Value;
                var nightWaking = classes[i - 1] == SleepClass.Night && classes[i] == SleepClass.Night;
                if (gap > p.LostTrackAfter)
                    position = null;
                else if (position is { } k && gap > TimeSpan.Zero && !nightWaking)
                {
                    // A window after a short nap was shortened (§4.4): counted at its usual length, so it does not drag the
                    // median down or widen the spread.
                    var afterShortNap = classes[i - 1] == SleepClass.Nap && sleeps[i - 1].Length < p.ShortNap;
                    var usual = afterShortNap ? gap / p.ShortNapFactor : gap;
                    observations.Add(new WakeWindowObservation(k, sleeps[i - 1].End!.Value, usual, classes[i] == SleepClass.Night, gap, afterShortNap));
                }
            }

            position = classes[i] == SleepClass.Night ? 1 : position + 1;
        }

        return (observations, position);
    }

    // No night logged lately: count today's naps since the morning.
    private static int FallbackPosition(IReadOnlyList<SleepSpan> sleeps, IReadOnlyList<SleepClass> classes, DateTimeOffset awake, TimeZoneInfo zone)
    {
        var localAwake = TimeZoneInfo.ConvertTime(awake, zone);
        var morning = new DateTimeOffset(localAwake.Date + NightWindow.Morning.ToTimeSpan(), localAwake.Offset);
        return 1 + sleeps.Where((s, i) => classes[i] == SleepClass.Nap && s.Start >= morning && s.End <= awake).Count();
    }

    internal static TimeSpan WeightedMedian(IReadOnlyList<TimeSpan> values, IReadOnlyList<double> weights)
    {
        var pairs = values.Zip(weights).OrderBy(x => x.First).ToList();
        var half = weights.Sum() / 2;
        var running = 0.0;
        foreach (var (value, weight) in pairs)
        {
            running += weight;
            if (running >= half)
                return value;
        }

        return pairs[^1].First;
    }

    internal static TimeSpan InterquartileRange(IReadOnlyList<TimeSpan> values)
    {
        var sorted = values.Order().ToList();
        return Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
    }

    private static TimeSpan Quantile(List<TimeSpan> sorted, double q)
    {
        var at = q * (sorted.Count - 1);
        var lower = (int)Math.Floor(at);
        var upper = (int)Math.Ceiling(at);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (at - lower));
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
