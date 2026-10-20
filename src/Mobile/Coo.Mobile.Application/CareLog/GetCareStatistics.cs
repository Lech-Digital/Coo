using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Mobile.Application.CareLog;

public enum CarePeriod
{
    Day,
    Week,
    Month,
}

public sealed record SleepStats(
    TimeSpan TotalSleep,
    TimeSpan DailyAverage,
    int TotalSessions,
    double SessionsPerDay,
    TimeSpan AverageDuration,
    TimeSpan LongestStretch);

public sealed record FeedStats(
    int TotalFeeds,
    double FeedsPerDay,
    int BreastFeeds,
    int BottleFeeds,
    TimeSpan TotalBreastDuration,
    TimeSpan AverageBreastDuration,
    TimeSpan AverageLeftDuration,
    TimeSpan AverageRightDuration,
    decimal TotalBottleMl,
    decimal AverageBottleMl);

public sealed record DiaperStats(
    int TotalDiapers,
    double DiapersPerDay,
    int WetCount,
    int DirtyCount,
    int MixedCount);

public sealed record PumpStats(
    decimal TotalMl,
    decimal DailyAverageMl,
    int TotalSessions,
    decimal AveragePerSessionMl);

public sealed record PeriodStats(
    CarePeriod Period,
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalDays,
    SleepStats Sleep,
    FeedStats Feeding,
    DiaperStats Diapers,
    PumpStats Pumping);

public sealed class GetCareStatistics(ICareLogStore store)
{
    public async Task<PeriodStats> ExecuteAsync(
        HouseholdId household,
        BabyId baby,
        DateOnly anchorDate,
        CarePeriod period,
        TimeZoneInfo zone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (startDate, endDate) = PeriodRange(anchorDate, period);
        var totalDays = Math.Max(1, endDate.DayNumber - startDate.DayNumber + 1);

        var startOffset = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(startDate.ToDateTime(TimeOnly.MinValue)));
        var endOffset = new DateTimeOffset(endDate.ToDateTime(TimeOnly.MaxValue), zone.GetUtcOffset(endDate.ToDateTime(TimeOnly.MaxValue)));

        // Include lookback so night sleeps starting on the eve of the period are captured
        var stored = await store.ListAsync(household, startOffset.AddDays(-1), endOffset, cancellationToken);
        var live = stored.OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => !e.IsDeleted && GetToday.IsAbout(e, baby))
            .ToList();

        // 1. Sleep statistics
        var sleepEntries = live.Where(e => e.Details is SleepDetails).ToList();
        var sleepDurations = new List<TimeSpan>();
        foreach (var e in sleepEntries)
        {
            var (sStart, sEnd) = e.Timing switch
            {
                SessionTiming s => (s.Range.Start, s.Range.End ?? now),
                _ => (e.Timing.OccurredAt, e.Timing.OccurredAt),
            };

            // Count if it ended within the period or started within the period
            if (sEnd >= startOffset && sStart <= endOffset)
            {
                var dur = sEnd - sStart;
                if (dur > TimeSpan.Zero)
                    sleepDurations.Add(dur);
            }
        }

        var totalSleep = TimeSpan.FromTicks(sleepDurations.Sum(d => d.Ticks));
        var dailyAvgSleep = TimeSpan.FromTicks(totalSleep.Ticks / totalDays);
        var avgSleepDuration = sleepDurations.Count > 0 ? TimeSpan.FromTicks(totalSleep.Ticks / sleepDurations.Count) : TimeSpan.Zero;
        var longestSleep = sleepDurations.Count > 0 ? sleepDurations.Max() : TimeSpan.Zero;
        var sleepStats = new SleepStats(
            totalSleep,
            dailyAvgSleep,
            sleepDurations.Count,
            Math.Round((double)sleepDurations.Count / totalDays, 1),
            avgSleepDuration,
            longestSleep);

        // 2. Feeding statistics
        var feedEntries = live.Where(e => e.Details is FeedingDetails && e.Timing.OccurredAt >= startOffset && e.Timing.OccurredAt <= endOffset).ToList();
        var breastFeeds = feedEntries.Where(e => e.Details is BreastFeed).ToList();
        var bottleFeeds = feedEntries.Where(e => e.Details is BottleFeed).ToList();

        var breastDurations = new List<TimeSpan>();
        var leftDurations = new List<TimeSpan>();
        var rightDurations = new List<TimeSpan>();

        foreach (var b in breastFeeds)
        {
            if (b.Details is BreastFeed bf)
            {
                var dur = bf.TotalDuration;
                if (dur > TimeSpan.Zero)
                    breastDurations.Add(dur);
                if (bf.LeftDuration > TimeSpan.Zero)
                    leftDurations.Add(bf.LeftDuration);
                if (bf.RightDuration > TimeSpan.Zero)
                    rightDurations.Add(bf.RightDuration);
            }
        }

        var totalBreastDur = TimeSpan.FromTicks(breastDurations.Sum(d => d.Ticks));
        var avgBreastDur = breastDurations.Count > 0 ? TimeSpan.FromTicks(totalBreastDur.Ticks / breastDurations.Count) : TimeSpan.Zero;
        var avgLeft = leftDurations.Count > 0 ? TimeSpan.FromTicks(leftDurations.Sum(d => d.Ticks) / leftDurations.Count) : TimeSpan.Zero;
        var avgRight = rightDurations.Count > 0 ? TimeSpan.FromTicks(rightDurations.Sum(d => d.Ticks) / rightDurations.Count) : TimeSpan.Zero;

        var totalBottleMl = bottleFeeds.Sum(b => ((BottleFeed)b.Details).Total.Millilitres);
        var avgBottleMl = bottleFeeds.Count > 0 ? Math.Round(totalBottleMl / bottleFeeds.Count, 0) : 0m;

        var feedStats = new FeedStats(
            feedEntries.Count,
            Math.Round((double)feedEntries.Count / totalDays, 1),
            breastFeeds.Count,
            bottleFeeds.Count,
            totalBreastDur,
            avgBreastDur,
            avgLeft,
            avgRight,
            totalBottleMl,
            avgBottleMl);

        // 3. Diaper statistics
        var diaperEntries = live.Where(e => e.Details is DiaperDetails && e.Timing.OccurredAt >= startOffset && e.Timing.OccurredAt <= endOffset).ToList();
        var wet = diaperEntries.Count(d => ((DiaperDetails)d.Details).Contents == DiaperContents.Wet);
        var dirty = diaperEntries.Count(d => ((DiaperDetails)d.Details).Contents == DiaperContents.Dirty);
        var mixed = diaperEntries.Count(d => ((DiaperDetails)d.Details).Contents == DiaperContents.Mixed);
        var diaperStats = new DiaperStats(
            diaperEntries.Count,
            Math.Round((double)diaperEntries.Count / totalDays, 1),
            wet,
            dirty,
            mixed);

        // 4. Pump statistics
        var pumpEntries = live.Where(e => e.Details is PumpDetails && e.Timing.OccurredAt >= startOffset && e.Timing.OccurredAt <= endOffset).ToList();
        var totalPumpMl = pumpEntries.Sum(p => ((PumpDetails)p.Details).Yield?.Total.Millilitres ?? 0m);
        var avgPumpMl = pumpEntries.Count > 0 ? Math.Round(totalPumpMl / pumpEntries.Count, 0) : 0m;
        var pumpStats = new PumpStats(
            totalPumpMl,
            Math.Round(totalPumpMl / totalDays, 0),
            pumpEntries.Count,
            avgPumpMl);

        return new PeriodStats(period, startDate, endDate, totalDays, sleepStats, feedStats, diaperStats, pumpStats);
    }

    public static (DateOnly Start, DateOnly End) PeriodRange(DateOnly anchor, CarePeriod period) => period switch
    {
        CarePeriod.Day => (anchor, anchor),
        CarePeriod.Week => (
            anchor.AddDays(-(int)anchor.DayOfWeek == 0 ? -6 : -(int)anchor.DayOfWeek + 1), // Monday
            anchor.AddDays(-(int)anchor.DayOfWeek == 0 ? 0 : 7 - (int)anchor.DayOfWeek)     // Sunday
        ),
        CarePeriod.Month => (
            new DateOnly(anchor.Year, anchor.Month, 1),
            new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month))
        ),
        _ => (anchor, anchor),
    };
}
