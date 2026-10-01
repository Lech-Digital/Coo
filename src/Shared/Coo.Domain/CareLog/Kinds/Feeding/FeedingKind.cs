using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Feeding;

/// <summary>The <c>feeding</c> kind: one kind, two methods (Spec 001 §7.2).</summary>
public sealed class FeedingKind : EntryKind<FeedingDetails>
{
    public static readonly FeedingKind Instance = new();

    private FeedingKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("feeding");

    public override SubjectType Subject => SubjectType.Baby;

    public override TimeSpan? RunningLongAfter => FeedingThresholds.RunningLongAfter;

    /// <summary>Same method only: a top-up bottle after the breast is normal, not a duplicate.</summary>
    public override DuplicateRule Duplicates { get; } =
        DuplicateRule.Within(FeedingThresholds.DuplicateTolerance, (a, b) => a.GetType() == b.GetType());

    protected override TimingRule TimingFor(FeedingDetails details) => details switch
    {
        BreastFeed => TimingRule.DerivedFromDetails,
        BottleFeed => TimingRule.PointOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(details), details.GetType().Name, "Unknown feeding method."),
    };

    protected override EntryTiming DeriveTiming(FeedingDetails details) => details is BreastFeed breast
        ? EntryTiming.Session(TimeRange.Create(breast.FirstStart, breast.IsCompleted ? breast.Segments[^1].End : null).Value)
        : base.DeriveTiming(details);

    protected override Result Validate(FeedingDetails details, EntryTiming timing) => details switch
    {
        BreastFeed breast => BreastFeed.Check(breast.Segments, breast.IsCompleted),
        _ => Result.Success,
    };

    protected override Result ValidateChange(FeedingDetails before, FeedingDetails after) =>
        Result.Check(before.GetType() == after.GetType(), FeedingErrors.MethodChangeNotAllowed);

    protected override IEnumerable<Warning> Plausibility(FeedingDetails details, EntryTiming timing, PlausibilityContext context)
    {
        if (details is BottleFeed bottle
            && bottle.Total.Millilitres is < FeedingThresholds.BottleWarnBelowMl or > FeedingThresholds.BottleWarnAboveMl)
            yield return PlausibilityCodes.BottleAmount;

        if (details is BreastFeed breast && breast.Span > FeedingThresholds.BreastLongWarning)
            yield return PlausibilityCodes.BreastLong;
    }
}
