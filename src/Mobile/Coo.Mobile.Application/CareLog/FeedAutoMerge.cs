using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// Rules for automatically merging feedings and resolving obvious duplicates without requiring manual review.
/// </summary>
public static class FeedAutoMerge
{
    public static readonly TimeSpan MaxFeedGap = FeedingThresholds.DuplicateTolerance; // 15 minutes
    public static readonly TimeSpan MaxTotalSpan = TimeSpan.FromHours(2.5);

    /// <summary>
    /// Checks whether an attention group of feedings can be merged automatically.
    /// E.g. Left breast for 5 mins, 3 mins later Right breast for 10 mins.
    /// </summary>
    public static bool CanAutoMerge(AttentionGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!group.CanMerge || group.Entries.Count < 2)
            return false;

        var entries = group.Entries.OrderBy(e => e.Timing.OccurredAt).ThenBy(e => e.Id.Value).ToList();

        if (entries[0].Details is BreastFeed)
            return CanAutoMergeBreast(entries);

        if (entries[0].Details is BottleFeed)
            return CanAutoMergeBottle(entries);

        return false;
    }

    private static bool CanAutoMergeBreast(List<CareEntry> entries)
    {
        var feeds = entries.Select(e => e.Details as BreastFeed).ToList();
        if (feeds.Any(f => f is null || !f.IsCompleted))
            return false;

        var firstStart = feeds[0]!.FirstStart;
        var lastEnd = feeds[^1]!.Segments[^1].End;
        if (lastEnd is null || (lastEnd.Value - firstStart) > MaxTotalSpan)
            return false;

        for (var i = 1; i < feeds.Count; i++)
        {
            var prevEnd = feeds[i - 1]!.Segments[^1].End;
            var currStart = feeds[i]!.FirstStart;

            if (prevEnd is null || currStart < prevEnd.Value)
                return false; // overlapping or running

            if ((currStart - prevEnd.Value) > MaxFeedGap)
                return false; // gap too large
        }

        return true;
    }

    private static bool CanAutoMergeBottle(List<CareEntry> entries)
    {
        var feeds = entries.Select(e => e.Details as BottleFeed).ToList();
        if (feeds.Any(f => f is null))
            return false;

        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Timing.OccurredAt - entries[i - 1].Timing.OccurredAt > MaxFeedGap)
                return false;
        }

        var totalMl = feeds.Sum(f => f!.Total.Millilitres);
        return totalMl <= 500m;
    }

    /// <summary>
    /// Checks whether two duplicate entries are identical enough to auto-resolve by keeping one and deleting the other.
    /// E.g., duplicate diaper or bottle logged simultaneously or replayed during offline sync.
    /// </summary>
    public static bool CanAutoResolveDuplicate(CareEntry a, CareEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Id == b.Id || a.IsDeleted || b.IsDeleted)
            return false;

        if (a.Kind != b.Kind || a.Subject != b.Subject)
            return false;

        var timeDiff = (a.Timing.OccurredAt - b.Timing.OccurredAt).Duration();
        if (timeDiff > TimeSpan.FromMinutes(2))
            return false;

        return (a.Details, b.Details) switch
        {
            (Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails d1, Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails d2) =>
                d1.Contents == d2.Contents,
            (BottleFeed b1, BottleFeed b2) =>
                b1.Total.Millilitres == b2.Total.Millilitres && b1.Components.Count == b2.Components.Count,
            (Coo.Domain.CareLog.Kinds.Sleep.SleepDetails s1, Coo.Domain.CareLog.Kinds.Sleep.SleepDetails s2) =>
                s1.KindOverride == s2.KindOverride && !a.Timing.IsInProgress && !b.Timing.IsInProgress,
            (Coo.Domain.CareLog.Kinds.Pump.PumpDetails p1, Coo.Domain.CareLog.Kinds.Pump.PumpDetails p2) =>
                p1.Yield?.Total.Millilitres == p2.Yield?.Total.Millilitres,
            _ => false,
        };
    }
}
