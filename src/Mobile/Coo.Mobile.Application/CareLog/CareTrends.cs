using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Mobile.Application.CareLog;

/// <summary>How long one bar of the trends chart is.</summary>
public enum TrendGrain
{
    Day,
    Week,
    Month,
}

/// <summary>
/// One local day's care, as the trends count it. Sleep is cut at midnight, like the Log's totals, so a night across
/// midnight is never counted twice; a sleep's count and length belong to the day it started. Everything else belongs to
/// the day it started on.
/// </summary>
public sealed record DayCare(
    DateOnly Date,
    bool HasEntries,
    TimeSpan Sleep,
    int Sleeps,
    TimeSpan LongestSleep,
    int Feeds,
    int BreastFeeds,
    TimeSpan BreastTime,
    decimal BottleMl,
    int Diapers,
    int Wet,
    int Dirty,
    decimal PumpedMl,
    int Bottles = 0);

/// <summary>
/// A day, week or month of the trends chart, as daily averages over <see cref="DaysCounted"/>: the days in it that
/// have entries and are over. Days nobody logged are left out, so a forgotten day does not drag the week down, and the
/// week in progress is not divided by days that have not happened yet. A week whose only logged day is today counts
/// today, and says so with <see cref="IsPartial"/>.
/// </summary>
public sealed record TrendBucket(
    DateOnly Start,
    DateOnly End,
    int DaysCounted,
    bool IsCurrent,
    bool IsPartial,
    TimeSpan Sleep,
    TimeSpan LongestSleep,
    double Sleeps,
    double Feeds,
    TimeSpan AverageBreastFeed,
    decimal BottleMl,
    double Diapers,
    double Wet,
    double Dirty,
    decimal PumpedMl)
{
    public bool HasData => DaysCounted > 0;
}

/// <summary>Daily averages per day, week or month, for comparing one with the one before.</summary>
public sealed class GetCareTrends(ICareLogStore store)
{
    /// <summary>
    /// The <paramref name="count"/> buckets that end with the one holding <paramref name="last"/>, oldest first.
    /// Days after today are never counted.
    /// </summary>
    public async Task<IReadOnlyList<TrendBucket>> ExecuteAsync(
        HouseholdId household,
        BabyId baby,
        TrendGrain grain,
        DateOnly last,
        int count,
        DayOfWeek firstDayOfWeek,
        TimeZoneInfo zone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var today = DayMath.DateOf(now, zone);
        var ranges = Enumerable.Range(0, count)
            .Select(i => Range(Shift(last, grain, i - count + 1), grain, firstDayOfWeek))
            .ToList();

        var from = ranges[0].Start;
        var to = ranges[^1].End < today ? ranges[^1].End : today;
        var days = from <= to
            ? await DaysAsync(household, baby, from, to, zone, now, cancellationToken)
            : new Dictionary<DateOnly, DayCare>();

        return [.. ranges.Select(r => Bucket(r.Start, r.End, days, today))];
    }

    /// <summary>The first and last day of the bucket that holds <paramref name="date"/>.</summary>
    public static (DateOnly Start, DateOnly End) Range(DateOnly date, TrendGrain grain, DayOfWeek firstDayOfWeek)
    {
        switch (grain)
        {
            case TrendGrain.Week:
                var start = date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
                return (start, start.AddDays(6));
            case TrendGrain.Month:
                var first = new DateOnly(date.Year, date.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            default:
                return (date, date);
        }
    }

    /// <summary><paramref name="date"/> moved by <paramref name="buckets"/> days, weeks or months.</summary>
    public static DateOnly Shift(DateOnly date, TrendGrain grain, int buckets) => grain switch
    {
        TrendGrain.Week => date.AddDays(7 * buckets),
        TrendGrain.Month => date.AddMonths(buckets),
        _ => date.AddDays(buckets),
    };

    /// <summary>Each day from <paramref name="from"/> to <paramref name="to"/>, as the trends count it.</summary>
    /// <param name="until">Counts each day only up to this time of day, so today can be compared with other days "by
    /// now" (UX review, 2026-11-06); null for whole days.</param>
    public async Task<IReadOnlyDictionary<DateOnly, DayCare>> DaysAsync(
        HouseholdId household,
        BabyId baby,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        TimeOnly? until = null)
    {
        var first = DayMath.Day(from, zone);
        var lastDay = DayMath.Day(to, zone);

        // From a day earlier, so a sleep that started the evening before is found.
        var stored = await store.ListAsync(household, first.Start.AddDays(-1), lastDay.End, cancellationToken);
        var entries = stored.OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => !e.IsDeleted && GetToday.IsAbout(e, baby));

        var days = new Dictionary<DateOnly, Tally>();
        Tally On(DateOnly date) => days.TryGetValue(date, out var t) ? t : days[date] = new Tally();
        bool InRange(DateOnly date) => date >= from && date <= to;
        DateTimeOffset CutOff(DateOnly date) => until is { } u ? DayMath.Day(date, zone).Start + u.ToTimeSpan() : DateTimeOffset.MaxValue;

        foreach (var entry in entries)
        {
            var startDate = DayMath.DateOf(entry.Timing.OccurredAt, zone);
            var counted = entry.Timing.OccurredAt <= CutOff(startDate);
            switch (entry.Details)
            {
                case SleepDetails when entry.Timing is SessionTiming session:
                    var end = session.Range.End ?? now;
                    for (var date = startDate; date <= DayMath.DateOf(end, zone) && date <= to; date = date.AddDays(1))
                    {
                        var cut = CutOff(date);
                        var overlap = DayMath.Day(date, zone).Overlap(session.Range.Start, end < cut ? end : cut);
                        if (!InRange(date) || overlap <= TimeSpan.Zero)
                            continue;
                        var tally = On(date);
                        tally.Sleep += overlap;
                        tally.HasEntries = true;
                    }

                    if (InRange(startDate) && counted)
                    {
                        var tally = On(startDate);
                        tally.Sleeps++;
                        if (session.Range.End is { } woke && woke - session.Range.Start > tally.LongestSleep)
                            tally.LongestSleep = woke - session.Range.Start;
                    }

                    break;
                case FeedingDetails feed when InRange(startDate) && counted:
                    var feeds = On(startDate);
                    feeds.HasEntries = true;
                    feeds.Feeds++;
                    if (feed is BreastFeed { IsCompleted: true } breast && breast.TotalDuration > TimeSpan.Zero)
                    {
                        feeds.BreastFeeds++;
                        feeds.BreastTime += breast.TotalDuration;
                    }
                    else if (feed is BottleFeed bottle)
                    {
                        feeds.Bottles++;
                        feeds.BottleMl += bottle.Total.Millilitres;
                    }

                    break;
                case DiaperDetails diaper when InRange(startDate) && counted:
                    var diapers = On(startDate);
                    diapers.HasEntries = true;
                    diapers.Diapers++;
                    // A mixed diaper is both wet and dirty, as a midwife counts them.
                    if (diaper.Contents is DiaperContents.Wet or DiaperContents.Mixed)
                        diapers.Wet++;
                    if (diaper.Contents is DiaperContents.Dirty or DiaperContents.Mixed)
                        diapers.Dirty++;
                    break;
                case PumpDetails pump when InRange(startDate) && counted:
                    var pumps = On(startDate);
                    pumps.HasEntries = true;
                    pumps.PumpedMl += pump.Yield?.Total.Millilitres ?? 0m;
                    break;
            }
        }

        var result = new Dictionary<DateOnly, DayCare>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var t = days.TryGetValue(date, out var found) ? found : new Tally();
            result[date] = new DayCare(
                date, t.HasEntries, t.Sleep, t.Sleeps, t.LongestSleep, t.Feeds, t.BreastFeeds, t.BreastTime, t.BottleMl,
                t.Diapers, t.Wet, t.Dirty, t.PumpedMl, t.Bottles);
        }

        return result;
    }

    private static TrendBucket Bucket(DateOnly start, DateOnly end, IReadOnlyDictionary<DateOnly, DayCare> days, DateOnly today)
    {
        var logged = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1)
            .Select(start.AddDays)
            .Where(d => d <= today && days.TryGetValue(d, out var day) && day.HasEntries)
            .Select(d => days[d])
            .ToList();

        // Today is still going: it counts only in a day of its own, or when nothing else in the bucket was logged.
        var over = logged.Where(d => d.Date < today).ToList();
        var counted = start == end || over.Count == 0 ? logged : over;
        var isCurrent = start <= today && today <= end;
        var isPartial = counted.Any(d => d.Date == today);

        var n = counted.Count;
        if (n == 0)
            return new TrendBucket(start, end, 0, isCurrent, isPartial, default, default, 0, 0, default, 0, 0, 0, 0, 0);

        var breastFeeds = counted.Sum(d => d.BreastFeeds);
        return new TrendBucket(
            start,
            end,
            n,
            isCurrent,
            isPartial,
            TimeSpan.FromTicks(counted.Sum(d => d.Sleep.Ticks) / n),
            TimeSpan.FromTicks(counted.Sum(d => d.LongestSleep.Ticks) / n),
            (double)counted.Sum(d => d.Sleeps) / n,
            (double)counted.Sum(d => d.Feeds) / n,
            breastFeeds > 0 ? TimeSpan.FromTicks(counted.Sum(d => d.BreastTime.Ticks) / breastFeeds) : TimeSpan.Zero,
            counted.Sum(d => d.BottleMl) / n,
            (double)counted.Sum(d => d.Diapers) / n,
            (double)counted.Sum(d => d.Wet) / n,
            (double)counted.Sum(d => d.Dirty) / n,
            counted.Sum(d => d.PumpedMl) / n);
    }

    private sealed class Tally
    {
        public bool HasEntries { get; set; }

        public TimeSpan Sleep { get; set; }

        public int Sleeps { get; set; }

        public TimeSpan LongestSleep { get; set; }

        public int Feeds { get; set; }

        public int BreastFeeds { get; set; }

        public TimeSpan BreastTime { get; set; }

        public decimal BottleMl { get; set; }

        public int Bottles { get; set; }

        public int Diapers { get; set; }

        public int Wet { get; set; }

        public int Dirty { get; set; }

        public decimal PumpedMl { get; set; }
    }
}

/// <summary>
/// "On an average day" over the last few days, as a doctor asks it: how much milk from bottles and in how many bottles,
/// how many feeds, how much sleep, how many diapers. Days nobody logged are left out, and today counts only while no
/// earlier day has entries, like the trends.
/// </summary>
public sealed record DailyAverages(
    DateOnly From,
    DateOnly To,
    int DaysCounted,
    bool IncludesToday,
    decimal BottleMl,
    double Bottles,
    double Feeds,
    double BreastFeeds,
    TimeSpan AverageBreastFeed,
    TimeSpan Sleep,
    TimeSpan LongestSleep,
    double Diapers,
    double Wet,
    double Dirty,
    decimal PumpedMl)
{
    public bool HasData => DaysCounted > 0;

    /// <summary>The average bottle, or null when no bottle was logged.</summary>
    public decimal? MlPerBottle => Bottles > 0 ? BottleMl / (decimal)Bottles : null;
}

/// <summary>The daily averages over the <c>days</c> days before today (today itself while nothing earlier is logged).</summary>
public sealed class GetDailyAverages(GetCareTrends trends)
{
    public async Task<DailyAverages> ExecuteAsync(
        HouseholdId household, BabyId baby, int days, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var today = DayMath.DateOf(now, zone);
        var from = today.AddDays(-days);
        var all = await trends.DaysAsync(household, baby, from, today, zone, now, cancellationToken);
        var over = all.Values.Where(d => d.Date < today && d.HasEntries).ToList();
        var includesToday = over.Count == 0 && all.TryGetValue(today, out var t) && t.HasEntries;
        var counted = includesToday ? [all[today]] : over;
        var n = counted.Count;
        if (n == 0)
            return new DailyAverages(from, today.AddDays(-1), 0, false, 0, 0, 0, 0, default, default, default, 0, 0, 0, 0);

        var breastFeeds = counted.Sum(d => d.BreastFeeds);
        return new DailyAverages(
            from,
            includesToday ? today : today.AddDays(-1),
            n,
            includesToday,
            counted.Sum(d => d.BottleMl) / n,
            (double)counted.Sum(d => d.Bottles) / n,
            (double)counted.Sum(d => d.Feeds) / n,
            (double)breastFeeds / n,
            breastFeeds > 0 ? TimeSpan.FromTicks(counted.Sum(d => d.BreastTime.Ticks) / breastFeeds) : TimeSpan.Zero,
            TimeSpan.FromTicks(counted.Sum(d => d.Sleep.Ticks) / n),
            TimeSpan.FromTicks(counted.Max(d => d.LongestSleep.Ticks)),
            (double)counted.Sum(d => d.Diapers) / n,
            (double)counted.Sum(d => d.Wet) / n,
            (double)counted.Sum(d => d.Dirty) / n,
            counted.Sum(d => d.PumpedMl) / n);
    }
}
