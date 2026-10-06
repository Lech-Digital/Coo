using Coo.Domain.CareLog.Kinds.Feeding;

namespace Coo.Mobile.Presentation.Formatting;

/// <summary>
/// A breast feed's parts (Spec 011d §2): runs of segments in which each starts at most
/// <see cref="FeedingThresholds.PartGap"/> after the previous ended, as <see cref="BreastFeed.Parts"/> counts them.
/// </summary>
public static class FeedParts
{
    public static IReadOnlyList<IReadOnlyList<BreastSegment>> Of(BreastFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var parts = new List<List<BreastSegment>> { new() { feed.Segments[0] } };
        foreach (var (previous, next) in feed.Segments.Zip(feed.Segments.Skip(1)))
        {
            if (next.Start - (previous.End ?? next.Start) > FeedingThresholds.PartGap)
                parts.Add([]);
            parts[^1].Add(next);
        }

        return parts;
    }
}
