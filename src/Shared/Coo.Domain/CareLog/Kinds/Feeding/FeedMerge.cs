using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Feeding;

/// <summary>Why feeds cannot be merged (Spec 011d §3). Checked in this order.</summary>
public enum FeedMergeRefusal
{
    /// <summary>Not all about one baby.</summary>
    DifferentBaby,

    /// <summary>A feed is read-only (quarantined, rejected) or already deleted.</summary>
    ReadOnly,

    /// <summary>Not all breast feeds, or not all bottle feeds.</summary>
    MixedVariant,

    /// <summary>A breast feed's timer is still running.</summary>
    Running,

    /// <summary>Two breast feeds overlap in time.</summary>
    Overlap,

    /// <summary>Two feeds in a row are more than the tolerance apart.</summary>
    NotClose,

    /// <summary>The merged breast feed would span more than six hours.</summary>
    TooLong,

    /// <summary>The merged bottle would hold more than 1000 ml.</summary>
    TooMuch,

    /// <summary>The joined notes would pass the note length limit.</summary>
    NoteTooLong,
}

/// <summary>
/// What a merge does: the earliest feed (<see cref="SurvivorId"/>) is revised to <see cref="Details"/> and
/// <see cref="Note"/>, and the feeds in <see cref="DeletedIds"/> are deleted.
/// </summary>
public sealed record FeedMergePlan(
    EntryId SurvivorId,
    FeedingDetails Details,
    Note? Note,
    IReadOnlyList<EntryId> DeletedIds);

/// <summary>A merge plan, or the reason there is none. Exactly one of the two is set.</summary>
public sealed record FeedMergeOutcome(FeedMergePlan? Plan, FeedMergeRefusal? Refusal)
{
    public bool IsMergeable => Plan is not null;
}

/// <summary>Merging feeds that are close together into one feed in parts (Spec 011d §3).</summary>
public static class FeedMerge
{
    public const string NoteSeparator = " · ";

    /// <summary>
    /// Plans merging two or more feeds, in any order. <paramref name="isReadOnly"/> tells which entries the
    /// device must not change (quarantined or rejected rows, ADR 0010 §3); the domain entry does not know.
    /// </summary>
    public static FeedMergeOutcome Plan(IReadOnlyCollection<CareEntry> feeds, Func<CareEntry, bool>? isReadOnly = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        if (feeds.Count < 2)
            throw new ArgumentException("Merging needs at least two feeds.", nameof(feeds));

        if (feeds.Any(f => f.Subject is not BabySubject))
            return Refuse(FeedMergeRefusal.MixedVariant);

        if (feeds.Select(f => ((BabySubject)f.Subject).BabyId).Distinct().Count() != 1)
            return Refuse(FeedMergeRefusal.DifferentBaby);

        if (feeds.Any(f => f.IsDeleted || isReadOnly?.Invoke(f) == true))
            return Refuse(FeedMergeRefusal.ReadOnly);

        if (feeds.Any(f => f.Details is not FeedingDetails)
            || feeds.Select(f => f.Details.GetType()).Distinct().Count() != 1)
            return Refuse(FeedMergeRefusal.MixedVariant);

        var ordered = feeds.OrderBy(f => f.Timing.OccurredAt).ToList();
        var survivor = ordered[0];
        var rest = ordered.Skip(1).Select(f => f.Id).ToList().AsReadOnly();

        var note = JoinNotes(ordered);
        if (note.IsFailure)
            return Refuse(FeedMergeRefusal.NoteTooLong);

        return survivor.Details is BreastFeed
            ? PlanBreast(ordered, rest, note.Value)
            : PlanBottle(ordered, rest, note.Value);
    }

    /// <summary>
    /// Whether two feeds are close together (Spec 011d §3 rule 3): same baby, same variant, and the later one
    /// starts within <see cref="FeedingThresholds.DuplicateTolerance"/> of the earlier one's end (breast) or time
    /// (bottle). Overlapping feeds are close; <see cref="Plan"/> refuses them as <see cref="FeedMergeRefusal.Overlap"/>.
    /// </summary>
    public static bool AreCloseTogether(CareEntry a, CareEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Subject is not BabySubject sa || b.Subject is not BabySubject sb || sa.BabyId != sb.BabyId)
            return false;

        if (a.Details is not FeedingDetails || a.Details.GetType() != b.Details.GetType())
            return false;

        var (earlier, later) = a.Timing.OccurredAt <= b.Timing.OccurredAt ? (a, b) : (b, a);
        return later.Timing.OccurredAt - EndOf(earlier) <= FeedingThresholds.DuplicateTolerance;
    }

    /// <summary>A breast feed's last known instant (its last end, or the start of an open segment); a bottle's time.</summary>
    private static DateTimeOffset EndOf(CareEntry entry) =>
        entry.Details is BreastFeed breast ? breast.Segments[^1].End ?? breast.Segments[^1].Start : entry.Timing.OccurredAt;

    private static FeedMergeOutcome PlanBreast(List<CareEntry> ordered, IReadOnlyList<EntryId> rest, Note? note)
    {
        var feeds = ordered.Select(e => (BreastFeed)e.Details).ToList();
        if (feeds.Any(f => !f.IsCompleted))
            return Refuse(FeedMergeRefusal.Running);

        var latestEnd = feeds[0].Segments[^1].End!.Value;
        for (var i = 1; i < feeds.Count; i++)
        {
            if (feeds[i].FirstStart < latestEnd)
                return Refuse(FeedMergeRefusal.Overlap);
            latestEnd = feeds[i].Segments[^1].End!.Value;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (feeds[i].FirstStart - feeds[i - 1].Segments[^1].End!.Value > FeedingThresholds.DuplicateTolerance)
                return Refuse(FeedMergeRefusal.NotClose);
        }

        var merged = BreastFeed.Create(feeds.SelectMany(f => f.Segments), isCompleted: true);
        if (merged.IsFailure)
            return Refuse(merged.Error!.Code == FeedingErrors.BreastTooLong ? FeedMergeRefusal.TooLong : FeedMergeRefusal.Overlap);

        return new FeedMergeOutcome(new FeedMergePlan(ordered[0].Id, merged.Value, note, rest), null);
    }

    private static FeedMergeOutcome PlanBottle(List<CareEntry> ordered, IReadOnlyList<EntryId> rest, Note? note)
    {
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Timing.OccurredAt - ordered[i - 1].Timing.OccurredAt > FeedingThresholds.DuplicateTolerance)
                return Refuse(FeedMergeRefusal.NotClose);
        }

        var perMilk = ordered
            .SelectMany(e => ((BottleFeed)e.Details).Components)
            .GroupBy(c => c.Milk)
            .Select(g => (Milk: g.Key, Ml: g.Sum(c => c.Volume.Millilitres)))
            .ToList();

        if (perMilk.Sum(m => m.Ml) > FeedingThresholds.BottleTotalMaxMl)
            return Refuse(FeedMergeRefusal.TooMuch);

        var components = perMilk.Select(m => new BottleComponent(m.Milk, Volume.FromMillilitres(m.Ml).Value));
        var merged = BottleFeed.Create(components);
        return merged.IsFailure
            ? Refuse(FeedMergeRefusal.TooMuch)
            : new FeedMergeOutcome(new FeedMergePlan(ordered[0].Id, merged.Value, note, rest), null);
    }

    private static Result<Note?> JoinNotes(IEnumerable<CareEntry> ordered) =>
        Note.From(string.Join(NoteSeparator, ordered.Select(e => e.Note?.Text).Where(t => !string.IsNullOrEmpty(t))));

    private static FeedMergeOutcome Refuse(FeedMergeRefusal refusal) => new(null, refusal);
}
