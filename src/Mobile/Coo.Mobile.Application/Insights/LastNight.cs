using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>
/// Last night, for the morning note (Spec 019 §2.3): how long she slept, how often she woke, the longest stretch, the
/// night feeds; and while a plan runs, the same measures for the week before it, to read last night against the goal.
/// </summary>
public sealed record LastNight(SleepMeasures Night, int Feeds, SleepMeasures? BeforePlan, IReadOnlyList<SleepPlanGoal> Goals);

public sealed class GetLastNight(ICareLogStore store)
{
    /// <summary>Until noon the night is news; after it, the day's own numbers take over.</summary>
    public const int ShownUntilHour = 12;

    /// <summary>The night, from 15:00 yesterday; null outside the morning, before the night has ended, or with no night logged.</summary>
    public async Task<LastNight?> ExecuteAsync(
        HouseholdId household, BabyProfile baby, SleepPlanToday? plan, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (!baby.IsBorn || local.Hour >= ShownUntilHour || local.Hour < 4)
            return null;

        var today = DateOnly.FromDateTime(local.DateTime);
        var from = At(today.AddDays(-1), new TimeOnly(15, 0), zone);
        var planStart = plan is null ? (DateTimeOffset?)null : At(plan.StartedOn, TimeOnly.MinValue, zone);
        var readFrom = planStart is { } started && started - TimeSpan.FromDays(8) < from ? started - TimeSpan.FromDays(8) : from;
        var entries = (await store.ListAsync(household, readFrom - TimeSpan.FromDays(1), now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => GetToday.IsAbout(e, baby.Id))
            .ToList();
        var sleeps = entries
            .Select(e => e is { Details: Coo.Domain.CareLog.Kinds.Sleep.SleepDetails details, Timing: SessionTiming timing }
                ? new SleepSpan(timing.Range.Start, timing.Range.End, details.KindOverride)
                : null)
            .OfType<SleepSpan>()
            .ToList();

        var night = SleepPlanner.Measure(sleeps, from, now, zone);
        if (night.NightSleep is null || night.Bedtime is not { } bedtime || night.WakeUp is not { } wakeUp)
            return null;

        // The night's feeds: from bedtime (last evening, or after midnight) to the morning's wake-up.
        var nightStart = At(bedtime.Hour >= 12 ? today.AddDays(-1) : today, bedtime, zone);
        var nightEnd = At(today, wakeUp, zone);
        var feeds = entries.Count(e => e.Details is FeedingDetails && e.Timing.OccurredAt >= nightStart && e.Timing.OccurredAt <= nightEnd);

        var before = planStart is { } start ? SleepPlanner.Measure(sleeps, start - TimeSpan.FromDays(7), start, zone) : null;
        return new LastNight(night, feeds, before is { NightSleep: not null } ? before : null, plan?.Details.Goals ?? []);
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
