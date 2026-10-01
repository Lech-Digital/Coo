using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Sleep;

/// <summary>The person's own label for a sleep, which always beats the classifier (ADR 0008 §3).</summary>
public enum SleepKindOverride
{
    Nap,
    Night,
}

/// <summary>A sleep (Spec 001 §7.4). The session is open while the baby sleeps.</summary>
public sealed record SleepDetails(SleepKindOverride? KindOverride = null) : EntryDetails
{
    public Result<SleepDetails> WithKindOverride(SleepKindOverride? kindOverride) => this with { KindOverride = kindOverride };
}

public static class SleepErrors
{
    public const string TooLong = "sleep.too-long";

    /// <summary>Checked by the application's EndSleep command, so a double tap does not move the end.</summary>
    public const string AlreadyEnded = "sleep.already-ended";
}

public static class SleepThresholds
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
    public static readonly TimeSpan LongWarning = TimeSpan.FromHours(16);
    public static readonly TimeSpan RunningLongAfter = TimeSpan.FromHours(16);
}

/// <summary>The <c>sleep</c> kind.</summary>
public sealed class SleepKind : EntryKind<SleepDetails>
{
    public static readonly SleepKind Instance = new();

    private SleepKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("sleep");

    public override SubjectType Subject => SubjectType.Baby;

    public override TimeSpan? RunningLongAfter => SleepThresholds.RunningLongAfter;

    public override DuplicateRule Duplicates => DuplicateRule.Overlapping;

    protected override TimingRule TimingFor(SleepDetails details) => TimingRule.SessionOnly;

    protected override Result Validate(SleepDetails details, EntryTiming timing) =>
        timing is SessionTiming { Range.End: { } end } session && end - session.Range.Start > SleepThresholds.MaxDuration
            ? Result.Failure(SleepErrors.TooLong)
            : Result.Success;

    protected override IEnumerable<Warning> Plausibility(SleepDetails details, EntryTiming timing, PlausibilityContext context)
    {
        if (timing is SessionTiming { Range.End: not null } session && session.Range.Duration(context.Now) > SleepThresholds.LongWarning)
            yield return PlausibilityCodes.SleepLong;
    }
}
