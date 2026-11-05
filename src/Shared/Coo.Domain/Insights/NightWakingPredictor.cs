namespace Coo.Domain.Insights;

/// <summary>A feed as the night predictor sees it: when it started and ended (a bottle without a length ends where it starts).</summary>
public sealed record FeedSpan(DateTimeOffset Start, DateTimeOffset? End);

/// <summary>
/// When a baby awake in the night is likely to be back asleep (owner, 2026-11-06: "at night it's more likely to eat and
/// go straight back to sleep"). Day wake windows do not apply at night: a night waking is a feed and back to sleep,
/// minutes rather than the hour or more a day window takes at the same age.
/// </summary>
/// <param name="FromFeed">Measured from the end of tonight's feed rather than from waking.</param>
/// <param name="FeedRunning">A feed is still running: the settle time is known, the clock time is not yet.</param>
/// <param name="Typical">Her usual time awake in such a waking, or her usual settle after a night feed.</param>
public sealed record NightReturn(
    DateTimeOffset AwakeSince,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    TimeSpan Typical,
    bool FromFeed,
    bool FeedRunning,
    int Observations,
    int Nights,
    string Version);

/// <summary>One night waking from the log: what came before it and how it ended.</summary>
internal sealed record NightWaking(
    DateTimeOffset From, TimeSpan Length, TimeSpan SleptBefore, int MinutesFromEvening, DateTimeOffset? FeedEnded, TimeSpan? SettleAfterFeed);

/// <summary>
/// The night-waking predictor: only the baby's own wakings, never an age table (the published figures are whole-night
/// totals from small studies, not a per-waking time to show a parent). Each past waking counts more the more it is like
/// tonight's: recent, after a stretch of about the same length, at about the same hour, and with or without a feed as
/// tonight. With a feed, the time from its end to sleep is steadier than the waking as a whole, so that is used once the
/// feed has ended.
/// </summary>
public static class NightWakingPredictor
{
    public const string Version = "night-v1";

    /// <summary>Wakings needed, on as many nights, before anything is shown.</summary>
    public const int MinimumWakings = 3;

    private static readonly TimeSpan LookBack = TimeSpan.FromDays(14);
    private const double HalfLifeDays = 7;

    /// <summary>How alike two stretches must be to count fully: the weight halves at about 1.5 times longer or shorter.</summary>
    private static readonly double StretchBandwidth = Math.Log(1.6);

    /// <summary>Minutes apart in the night at which a waking counts about half.</summary>
    private const double HourBandwidthMinutes = 100;

    /// <summary>Every waking keeps this much weight, so a night unlike any other falls back to her plain median.</summary>
    private const double Floor = 0.25;

    private static readonly TimeSpan BandMin = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan BandMax = TimeSpan.FromMinutes(25);

    /// <summary>A feed counts for a waking when it started in it, or up to this long before it ended.</summary>
    private static readonly TimeSpan FeedSlack = TimeSpan.FromMinutes(5);

    /// <param name="sleeps">The baby's sleeps of at least the last fortnight, any order.</param>
    /// <param name="feeds">Her feeds over the same time; none is fine.</param>
    /// <returns>Null unless she is awake now after a night sleep, in the night, with enough wakings of her own.</returns>
    public static NightReturn? Predict(IEnumerable<SleepSpan> sleeps, IEnumerable<FeedSpan> feeds, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(zone);
        var recent = sleeps.Where(s => s.Start <= now && s.Start >= now - LookBack - TimeSpan.FromDays(1)).OrderBy(s => s.Start).ToList();
        if (recent.Count == 0 || recent.Any(s => s.End is null || s.End > now))
            return null;

        var window = SleepClassifier.LearnNightWindow(recent, zone);
        var classes = SleepClassifier.Classify(recent, window, zone);
        var last = recent[^1];
        var awake = last.End!.Value;
        if (classes[^1] != SleepClass.Night || !window.IsNightTime(SleepClassifier.Local(now, zone)) || now - awake > SleepClassifier.NightWakingAtMost)
            return null;

        var feedList = feeds.OrderBy(f => f.Start).ToList();
        var wakings = Observe(recent, classes, feedList, zone).Where(w => w.From >= now - LookBack).ToList();
        var nights = wakings.Select(w => Night(w.From, zone)).Distinct().Count();
        if (wakings.Count < MinimumWakings || nights < MinimumWakings)
            return null;

        // Tonight's waking so far: the stretch before it, the hour, and any feed in it.
        var sleptBefore = last.Length ?? TimeSpan.Zero;
        var minutes = MinutesFromEvening(awake, zone);
        var feed = feedList.LastOrDefault(f => f.Start >= awake - FeedSlack && f.Start <= now);
        double Weight(NightWaking w) =>
            Math.Pow(0.5, (now - w.From).TotalDays / HalfLifeDays)
            * (Floor + ((1 - Floor) * Similar(w.SleptBefore, sleptBefore) * Close(w.MinutesFromEvening, minutes)));

        if (feed is not null)
        {
            var fed = wakings.Where(w => w.SettleAfterFeed is not null).ToList();
            if (fed.Count >= MinimumWakings)
            {
                var settle = SleepPredictor.WeightedMedian([.. fed.Select(w => w.SettleAfterFeed!.Value)], [.. fed.Select(Weight)]);
                var band = Band([.. fed.Select(w => w.SettleAfterFeed!.Value)]);
                if (feed.End is not { } ended || ended > now)
                    return new NightReturn(awake, null, null, settle, FromFeed: true, FeedRunning: true, fed.Count, nights, Version);
                var centre = ended + settle;
                return new NightReturn(awake, Later(centre - band, now), centre + band, settle, true, false, fed.Count, nights, Version);
            }
        }

        // No feed yet, or too few fed wakings to learn from: the waking as a whole, from when she woke.
        var typical = SleepPredictor.WeightedMedian([.. wakings.Select(w => w.Length)], [.. wakings.Select(Weight)]);
        var spread = Band([.. wakings.Select(w => w.Length)]);
        var at = awake + typical;
        return new NightReturn(awake, Later(at - spread, now), at + spread, typical, false, false, wakings.Count, nights, Version);
    }

    /// <summary>
    /// The wakings between two night sleeps of the same night, with the stretch before each and the feed in it. The last
    /// waking of a night (the morning) has no sleep after it and is not one.
    /// </summary>
    internal static List<NightWaking> Observe(
        IReadOnlyList<SleepSpan> sleeps, IReadOnlyList<SleepClass> classes, IReadOnlyList<FeedSpan> feeds, TimeZoneInfo zone)
    {
        var wakings = new List<NightWaking>();
        for (var i = 1; i < sleeps.Count; i++)
        {
            if (classes[i - 1] != SleepClass.Night || classes[i] != SleepClass.Night)
                continue;
            var from = sleeps[i - 1].End!.Value;
            var to = sleeps[i].Start;
            var gap = to - from;
            if (gap <= TimeSpan.Zero || gap > SleepClassifier.NightWakingAtMost)
                continue;

            var feed = feeds.LastOrDefault(f => f.Start >= from - FeedSlack && f.Start < to);
            var ended = feed is null ? (DateTimeOffset?)null : feed.End is { } e && e <= to ? e : feed.Start;
            TimeSpan? settle = ended is { } end ? to - end : null;
            wakings.Add(new NightWaking(from, gap, sleeps[i - 1].Length ?? TimeSpan.Zero, MinutesFromEvening(from, zone), ended, settle));
        }

        return wakings;
    }

    // Weight 1 for the same stretch, about a half at 1.6 times longer or shorter.
    private static double Similar(TimeSpan a, TimeSpan b)
    {
        if (a <= TimeSpan.Zero || b <= TimeSpan.Zero)
            return 1;
        var d = Math.Log(a.TotalMinutes / b.TotalMinutes) / StretchBandwidth;
        return Math.Exp(-0.7 * d * d);
    }

    private static double Close(int a, int b)
    {
        var d = (a - b) / HourBandwidthMinutes;
        return Math.Exp(-0.7 * d * d);
    }

    // Minutes since 18:00 local, so a night reads as one run of numbers across midnight.
    private static int MinutesFromEvening(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = SleepClassifier.Local(at, zone);
        return (((local.Hour * 60) + local.Minute) - (18 * 60) + (24 * 60)) % (24 * 60);
    }

    // The night a waking belongs to: the evening's date (a waking at 02:00 belongs to the day before).
    private static DateOnly Night(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime.AddHours(-12));

    private static TimeSpan Band(IReadOnlyList<TimeSpan> values)
    {
        var iqr = values.Count >= 2 ? SleepPredictor.InterquartileRange(values) : BandMax;
        return iqr < BandMin ? BandMin : iqr > BandMax ? BandMax : iqr;
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
