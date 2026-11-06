namespace Coo.Domain.Insights;

/// <summary>
/// When the next feed is likely (owner, 2026-11-06), from the baby's own feeds: the time between feeds is not the same
/// all day. Night feeds are usually further apart once nights lengthen, and many babies cluster-feed in the evening.
/// </summary>
/// <param name="LastFeed">When the last feed started.</param>
/// <param name="Typical">Her usual time from one feed's start to the next at this hour.</param>
/// <param name="Cluster">She usually cluster-feeds at this hour: feeds come close together, so no time is given.</param>
public sealed record FeedPrediction(
    DateTimeOffset LastFeed,
    DateTimeOffset Earliest,
    DateTimeOffset Latest,
    TimeSpan Typical,
    bool Cluster,
    int Intervals,
    int Days,
    string Version);

/// <summary>
/// The feed predictor: the recency-weighted median of her own feed-to-feed intervals, each weighted by how close in the
/// day it began to the last feed (an interval that began at 3 AM says more about the next feed after a 3 AM feed than one
/// that began at noon). Feeds a few minutes apart (the other side, a top-up) count as one feed. Nothing before 10
/// intervals on 3 days; no time at all in her usual cluster-feeding hours.
/// </summary>
public static class FeedPredictor
{
    public const string Version = "feed-v1";

    public const int MinimumIntervals = 10;

    private const int MinimumDays = 3;
    private const double HalfLifeDays = 7;
    private const double HourBandwidthMinutes = 120;
    private const double Floor = 0.2;

    private static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    /// <summary>Feeds that start within this long of the last one's start are the same feed.</summary>
    public static readonly TimeSpan SameFeed = TimeSpan.FromMinutes(30);

    /// <summary>Intervals longer than this are a gap in the log, not an interval.</summary>
    private static readonly TimeSpan LongestInterval = TimeSpan.FromHours(8);

    /// <summary>Three feeds within this span are a cluster.</summary>
    private static readonly TimeSpan ClusterSpan = TimeSpan.FromHours(3);

    private static readonly TimeSpan BandMin = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BandMax = TimeSpan.FromMinutes(45);

    /// <param name="feedStarts">When each feed started, any order, over at least the last fortnight.</param>
    public static FeedPrediction? Predict(IEnumerable<DateTimeOffset> feedStarts, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(feedStarts);
        ArgumentNullException.ThrowIfNull(zone);
        var feeds = Sessions(feedStarts.Where(f => f <= now && f >= now - LookBack - TimeSpan.FromDays(1)).Order());
        if (feeds.Count < 2)
            return null;

        var last = feeds[^1];
        var intervals = new List<(DateTimeOffset From, TimeSpan Length)>();
        for (var i = 1; i < feeds.Count; i++)
        {
            var gap = feeds[i] - feeds[i - 1];
            if (gap <= LongestInterval && feeds[i - 1] >= now - LookBack)
                intervals.Add((feeds[i - 1], gap));
        }

        var days = intervals.Select(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.From, zone).DateTime)).Distinct().Count();
        if (intervals.Count < MinimumIntervals || days < MinimumDays)
            return null;

        var minute = MinuteOfDay(last, zone);
        var weights = intervals
            .Select(x => Math.Pow(0.5, (now - x.From).TotalDays / HalfLifeDays) * (Floor + ((1 - Floor) * Close(MinuteOfDay(x.From, zone), minute))))
            .ToList();
        var typical = SleepPredictor.WeightedMedian([.. intervals.Select(x => x.Length)], weights);

        // The spread of the intervals that began within two hours of this time of day, or of all when too few.
        var near = intervals.Where(x => Distance(MinuteOfDay(x.From, zone), minute) <= 120).Select(x => x.Length).ToList();
        var iqr = SleepPredictor.InterquartileRange(near.Count >= 4 ? near : [.. intervals.Select(x => x.Length)]);
        var band = iqr < BandMin ? BandMin : iqr > BandMax ? BandMax : iqr;
        var centre = last + typical;
        return new FeedPrediction(last, centre - band, centre + band, typical, IsClusterTime(feeds, now, zone), intervals.Count, days, Version);
    }

    /// <summary>
    /// Feeds starting within <see cref="SameFeed"/> of the previous one merged into it (the other breast, a top-up
    /// bottle), so they do not read as a ten-minute interval.
    /// </summary>
    internal static List<DateTimeOffset> Sessions(IEnumerable<DateTimeOffset> ordered)
    {
        var sessions = new List<DateTimeOffset>();
        foreach (var start in ordered)
        {
            if (sessions.Count == 0 || start - sessions[^1] > SameFeed)
                sessions.Add(start);
        }

        return sessions;
    }

    /// <summary>
    /// Her cluster-feeding hours: on at least 3 of the last 7 days, three or more feeds within 3 hours that started
    /// within 90 minutes of this time of day.
    /// </summary>
    internal static bool IsClusterTime(IReadOnlyList<DateTimeOffset> feeds, DateTimeOffset now, TimeZoneInfo zone)
    {
        var minute = MinuteOfDay(now, zone);
        var clusterDays = new HashSet<DateOnly>();
        for (var i = 0; i + 2 < feeds.Count; i++)
        {
            if (feeds[i] < now - TimeSpan.FromDays(7) || feeds[i + 2] - feeds[i] > ClusterSpan)
                continue;
            if (Distance(MinuteOfDay(feeds[i], zone), minute) <= 90 || Distance(MinuteOfDay(feeds[i + 2], zone), minute) <= 90)
                clusterDays.Add(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(feeds[i], zone).DateTime));
        }

        return clusterDays.Count >= 3;
    }

    private static int MinuteOfDay(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return (local.Hour * 60) + local.Minute;
    }

    // Minutes apart on the clock, the short way round midnight.
    private static int Distance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, (24 * 60) - d);
    }

    private static double Close(int a, int b)
    {
        var d = Distance(a, b) / HourBandwidthMinutes;
        return Math.Exp(-0.7 * d * d);
    }
}
