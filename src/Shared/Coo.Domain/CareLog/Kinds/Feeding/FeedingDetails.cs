namespace Coo.Domain.CareLog.Kinds.Feeding;

/// <summary>
/// How a baby was fed (Spec 001 §7.2). The method is fixed once logged: a breast feed never becomes a bottle.
/// </summary>
#pragma warning disable CA1012 // Abstract records need a constructor the subtypes can reach.
public abstract record FeedingDetails : EntryDetails;
#pragma warning restore CA1012

public static class FeedingErrors
{
    public const string MethodChangeNotAllowed = "feeding.method-change-not-allowed";
    public const string BreastNoSegments = "breast.no-segments";
    public const string BreastSegmentsOverlap = "breast.segments-overlap";
    public const string BreastOpenSegmentNotLast = "breast.open-segment-not-last";
    public const string BreastCompletedWithOpenSegment = "breast.completed-with-open-segment";
    public const string BreastTooLong = "breast.too-long";
    public const string BreastSideDurationInvalid = "breast.side-duration-invalid";
    public const string BreastNoOpenSegment = "breast.no-open-segment";
    public const string BreastSegmentAlreadyOpen = "breast.segment-already-open";
    public const string BottleNoComponents = "bottle.no-components";
    public const string BottleDuplicateMilk = "bottle.duplicate-milk";
    public const string BottleTotalOutOfRange = "bottle.total-out-of-range";
}

/// <summary>The feeding kind's limits: hard rules and plausibility thresholds (Spec 001 §7.2, §8).</summary>
public static class FeedingThresholds
{
    public static readonly TimeSpan BreastMaxSpan = TimeSpan.FromHours(6);
    public const int SideMinutesMin = 1;
    public const int SideMinutesMax = 240;
    public const decimal BottleTotalMaxMl = 1000m;

    public static readonly TimeSpan BreastLongWarning = TimeSpan.FromMinutes(90);
    public const decimal BottleWarnBelowMl = 5m;
    public const decimal BottleWarnAboveMl = 400m;
    public static readonly TimeSpan RunningLongAfter = TimeSpan.FromHours(3);
    public static readonly TimeSpan DuplicateTolerance = TimeSpan.FromMinutes(15);
}
