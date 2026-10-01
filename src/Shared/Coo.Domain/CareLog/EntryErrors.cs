namespace Coo.Domain.CareLog;

/// <summary>Error codes of the entry core and of time rules (Spec 001 §13).</summary>
public static class EntryErrors
{
    public const string KindUnknown = "kind.unknown";
    public const string SubjectNotAllowed = "entry.subject-not-allowed";
    public const string TimingShapeNotAllowed = "entry.timing-shape-not-allowed";
    public const string TimingDerived = "entry.timing-derived";
    public const string DetailsTypeMismatch = "entry.details-type-mismatch";
    public const string TooFarInFuture = "time.too-far-in-future";

    /// <summary>No instant may be further ahead of <c>now</c> than this; it matches the HLC bound.</summary>
    public static readonly TimeSpan MaxAheadOfNow = TimeSpan.FromHours(24);
}
