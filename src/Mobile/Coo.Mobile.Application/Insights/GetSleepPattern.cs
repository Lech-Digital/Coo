using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>A sleep in one column of the pattern, as fractions of the column (0 is noon, 1 the next noon).</summary>
/// <param name="IsLongest">The longest sleep of the column, drawn in full colour.</param>
public sealed record PatternSleep(double From, double To, bool IsLongest);

/// <summary>One column: the day that ends with this evening's night, noon to noon.</summary>
/// <param name="Evening">The date the column's evening falls on.</param>
/// <param name="Feeds">When feeds started, as fractions of the column.</param>
/// <param name="Diapers">When diapers were changed, as fractions of the column.</param>
/// <param name="Logged">Anything was logged in it; an empty column is a day nobody logged, not a day without sleep.</param>
public sealed record PatternDay(
    DateOnly Evening, IReadOnlyList<PatternSleep> Sleeps, IReadOnlyList<double> Feeds, bool Logged, TimeSpan Longest, TimeOnly? LongestStart,
    IReadOnlyList<double>? Diapers = null);

/// <summary>
/// The days side by side, noon to noon, so a night stays in one piece (owner, 2026-11-06: "by looking at them next to each
/// other we can see that she usually sleeps between 7 pm and 1 am"), with when her longest sleep has usually started and
/// how long it has lasted.
/// </summary>
/// <param name="UsualLongestStart">The median start of each day's longest sleep, over the logged days; null with fewer than 3.</param>
public sealed record SleepPattern(IReadOnlyList<PatternDay> Days, TimeOnly? UsualLongestStart, TimeSpan? UsualLongest);

/// <summary>Reads the sleeps and feeds of the last days and lays them out as <see cref="SleepPattern"/>.</summary>
public sealed class GetSleepPattern(ICareLogStore store)
{
    /// <summary>Days needed before the usual start of the longest sleep is given.</summary>
    public const int DaysToSummarise = 3;

    /// <param name="days">How many columns, the last one holding <paramref name="now"/>.</param>
    public async Task<SleepPattern> ExecuteAsync(
        HouseholdId household, BabyId baby, int days, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var lastEvening = LastEvening(now, zone);
        var from = Noon(lastEvening.AddDays(-(days - 1)), zone);
        var stored = await store.ListAsync(household, from - TimeSpan.FromDays(1), now.AddMinutes(1), cancellationToken);
        var entries = stored.OfType<LiveEntry>().Select(l => l.Entry).Where(e => GetToday.IsAbout(e, baby)).ToList();
        var sleeps = entries
            .Where(e => e.Details is SleepDetails && e.Timing is SessionTiming)
            .Select(e => ((SessionTiming)e.Timing).Range)
            .Select(r => (r.Start, End: r.End ?? now))
            .ToList();
        var feeds = entries.Where(e => e.Details is FeedingDetails).Select(e => e.Timing.OccurredAt).ToList();
        var diapers = entries.Where(e => e.Details is Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails).Select(e => e.Timing.OccurredAt).ToList();
        return Build(sleeps, feeds, lastEvening, days, zone, now, diapers);
    }

    /// <summary>The column holding <paramref name="now"/>: before noon it is the one that began yesterday at noon.</summary>
    public static DateOnly LastEvening(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < 12 ? date.AddDays(-1) : date;
    }

    /// <summary>Lays the sleeps and feeds out in columns, oldest first; sleeps across noon are cut at it.</summary>
    public static SleepPattern Build(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> sleeps, IReadOnlyList<DateTimeOffset> feeds, DateOnly lastEvening, int days,
        TimeZoneInfo zone, DateTimeOffset now, IReadOnlyList<DateTimeOffset>? diapers = null)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(zone);
        var columns = new List<PatternDay>(days);
        for (var d = days - 1; d >= 0; d--)
        {
            var evening = lastEvening.AddDays(-d);
            var start = Noon(evening, zone);
            var end = Noon(evening.AddDays(1), zone);
            var length = (end - start).TotalMinutes;
            double At(DateTimeOffset t) => Math.Clamp((t - start).TotalMinutes / length, 0, 1);

            var pieces = sleeps
                .Where(s => s.End > start && s.Start < end && s.End > s.Start)
                .Select(s => (Start: s.Start < start ? start : s.Start, End: s.End > end ? end : s.End))
                .OrderBy(s => s.Start)
                .ToList();
            var longest = pieces.Count == 0 ? default : pieces.MaxBy(s => s.End - s.Start);
            var columnFeeds = feeds.Where(f => f >= start && f < end).Select(At).ToList();
            var columnDiapers = (diapers ?? []).Where(f => f >= start && f < end).Select(At).ToList();
            columns.Add(new PatternDay(
                evening,
                [.. pieces.Select(s => new PatternSleep(At(s.Start), At(s.End), s == longest))],
                columnFeeds,
                pieces.Count > 0 || columnFeeds.Count > 0 || columnDiapers.Count > 0,
                pieces.Count == 0 ? TimeSpan.Zero : longest.End - longest.Start,
                pieces.Count == 0 ? null : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(longest.Start, zone).DateTime),
                columnDiapers));
        }

        // Only days that are over: today's longest may still be running.
        var finished = columns.Where(c => c.LongestStart is not null && Noon(c.Evening.AddDays(1), zone) <= now).ToList();
        if (finished.Count < DaysToSummarise)
            return new SleepPattern(columns, null, null);

        var starts = finished.Select(c => FromNoon(c.LongestStart!.Value)).Order().ToList();
        var lengths = finished.Select(c => c.Longest).Order().ToList();
        return new SleepPattern(columns, new TimeOnly(12, 0).AddMinutes(starts[starts.Count / 2]), lengths[lengths.Count / 2]);
    }

    // Minutes from noon, so starts on both sides of midnight sort as one evening.
    private static int FromNoon(TimeOnly time) => (((time.Hour * 60) + time.Minute) - (12 * 60) + (24 * 60)) % (24 * 60);

    private static DateTimeOffset Noon(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
