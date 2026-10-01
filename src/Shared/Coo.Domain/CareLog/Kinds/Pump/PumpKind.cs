using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Pump;

/// <summary>How much was pumped: per side, or only a total (Spec 001 §7.5).</summary>
public abstract record PumpYield
{
    private protected PumpYield()
    {
    }

    public abstract Volume Total { get; }

    public static Result<PumpYield> BySide(Volume? left, Volume? right)
    {
        if (left is null && right is null)
            return Result<PumpYield>.Failure(PumpErrors.YieldEmpty);

        var total = Volume.Sum(new[] { left, right }.OfType<Volume>());
        return total.IsSuccess
            ? new PumpYieldBySide(left, right, total.Value)
            : Result<PumpYield>.Failure(total.Error!);
    }

    public static PumpYield OfTotal(Volume total) => new PumpYieldTotal(total);
}

public sealed record PumpYieldBySide(Volume? Left, Volume? Right, Volume SideTotal) : PumpYield
{
    public override Volume Total => SideTotal;
}

public sealed record PumpYieldTotal(Volume Amount) : PumpYield
{
    public override Volume Total => Amount;
}

/// <summary>A pumping session. A completed session without a yield is valid: the amount can come later.</summary>
public sealed record PumpDetails(PumpYield? Yield = null) : EntryDetails
{
    public Result<PumpDetails> WithYield(PumpYield? yield) => this with { Yield = yield };
}

public static class PumpErrors
{
    public const string YieldEmpty = "pump.yield-empty";
    public const string TooLong = "pump.too-long";
}

public static class PumpThresholds
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);
    public const decimal WarnAboveMl = 500m;
    public static readonly TimeSpan RunningLongAfter = TimeSpan.FromHours(3);
    public static readonly TimeSpan DuplicateTolerance = TimeSpan.FromMinutes(15);
}

/// <summary>The <c>pump</c> kind. Its subject is the person who pumped, not a baby.</summary>
public sealed class PumpKind : EntryKind<PumpDetails>
{
    public static readonly PumpKind Instance = new();

    private PumpKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("pump");

    public override SubjectType Subject => SubjectType.Caregiver;

    public override TimeSpan? RunningLongAfter => PumpThresholds.RunningLongAfter;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(PumpThresholds.DuplicateTolerance);

    protected override TimingRule TimingFor(PumpDetails details) => TimingRule.SessionOnly;

    protected override Result Validate(PumpDetails details, EntryTiming timing) =>
        timing is SessionTiming { Range.End: { } end } session && end - session.Range.Start > PumpThresholds.MaxDuration
            ? Result.Failure(PumpErrors.TooLong)
            : Result.Success;

    protected override IEnumerable<Warning> Plausibility(PumpDetails details, EntryTiming timing, PlausibilityContext context)
    {
        if (details.Yield is { } yield && yield.Total.Millilitres > PumpThresholds.WarnAboveMl)
            yield return PlausibilityCodes.PumpAmount;
    }
}
