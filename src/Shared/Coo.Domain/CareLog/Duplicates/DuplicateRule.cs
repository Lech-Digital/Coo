namespace Coo.Domain.CareLog.Duplicates;

/// <summary>How a kind decides that two of its entries are probably the same event (Spec 001 §9.1).</summary>
public abstract record DuplicateRule
{
    private protected DuplicateRule()
    {
    }

    public abstract bool Matches(CareEntry a, CareEntry b, DateTimeOffset now);

    /// <summary><c>OccurredAt</c> within the tolerance, inclusive; optionally only for the same variant.</summary>
    public static DuplicateRule Within(TimeSpan tolerance, Func<EntryDetails, EntryDetails, bool>? sameVariant = null) =>
        new WithinRule(tolerance, sameVariant);

    /// <summary>Session ranges overlap; an open range ends at <c>now</c>.</summary>
    public static DuplicateRule Overlapping { get; } = new OverlappingRule();

    private sealed record WithinRule(TimeSpan Tolerance, Func<EntryDetails, EntryDetails, bool>? SameVariant) : DuplicateRule
    {
        public override bool Matches(CareEntry a, CareEntry b, DateTimeOffset now) =>
            (a.Timing.OccurredAt - b.Timing.OccurredAt).Duration() <= Tolerance
            && (SameVariant is null || SameVariant(a.Details, b.Details));
    }

    private sealed record OverlappingRule : DuplicateRule
    {
        public override bool Matches(CareEntry a, CareEntry b, DateTimeOffset now) =>
            a.Timing is SessionTiming x && b.Timing is SessionTiming y && x.Range.Overlaps(y.Range, now);
    }
}
