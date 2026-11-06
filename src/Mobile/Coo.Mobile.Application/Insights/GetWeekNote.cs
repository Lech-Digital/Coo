using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>What changed this week, for Today's "This week" and the Advisor's questions.</summary>
public sealed record WeekNote(WeekComparison Comparison, NapTransition? Naps);

/// <summary>This week against the one before from the care trends, and any nap transition from the sleeps.</summary>
public sealed class GetWeekNote(ICareLogStore store, GetCareTrends trends)
{
    public async Task<WeekNote?> ExecuteAsync(HouseholdId household, BabyProfile baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        if (!baby.IsBorn)
            return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = await trends.DaysAsync(household, baby.Id, today.AddDays(-14), today.AddDays(-1), zone, now, cancellationToken);
        var comparison = WeekComparison.Of(
            [.. days.Values.Where(d => d.Date >= today.AddDays(-7))],
            [.. days.Values.Where(d => d.Date < today.AddDays(-7))]);

        var stored = await store.ListAsync(household, now - TimeSpan.FromDays(17), now.AddMinutes(1), cancellationToken);
        var sleeps = stored
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => GetToday.IsAbout(e, baby.Id) && e is { Details: SleepDetails, Timing: SessionTiming })
            .Select(e => (e.Details, Range: ((SessionTiming)e.Timing).Range))
            .Select(x => new SleepSpan(x.Range.Start, x.Range.End, ((SleepDetails)x.Details).KindOverride));
        return new WeekNote(comparison, NapTransitions.Detect(sleeps, now, zone));
    }
}
