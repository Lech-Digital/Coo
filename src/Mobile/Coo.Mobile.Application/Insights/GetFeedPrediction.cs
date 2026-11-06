using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Insights;

/// <summary>When the next feed is likely, from the baby's feeds of the last fortnight (<see cref="FeedPredictor"/>).</summary>
public sealed class GetFeedPrediction(ICareLogStore store)
{
    private static readonly TimeSpan Reach = TimeSpan.FromDays(15);

    /// <returns>Null for a baby not born yet, or before ten intervals on three days.</returns>
    public async Task<FeedPrediction?> ExecuteAsync(
        HouseholdId household, BabyProfile baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        if (!baby.IsBorn)
            return null;
        var stored = await store.ListAsync(household, now - Reach, now.AddMinutes(1), cancellationToken);
        var starts = stored
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => e.Details is FeedingDetails && GetToday.IsAbout(e, baby.Id))
            .Select(e => e.Timing.OccurredAt);
        return FeedPredictor.Predict(starts, now, zone);
    }
}
