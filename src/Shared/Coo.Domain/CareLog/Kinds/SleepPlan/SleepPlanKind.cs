using Coo.Domain.Common;
using Coo.Domain.Insights;

namespace Coo.Domain.CareLog.Kinds.SleepPlan;

/// <summary>A change a check-in suggested and the parent accepted, on the day she did (Spec 019 §2.4).</summary>
public sealed record AcceptedAdjustment(DateOnly On, PlanAdjustmentKind Kind, int Minutes);

/// <summary>
/// A sleep plan (Spec 019 §7): the parent's answers, the changes she accepted, and the day she stopped it. The entry's
/// time is the day it started. The planned day itself is not stored: it is computed every day from these, the baby's age
/// and her log (<see cref="SleepPlanner"/>), so the plan adapts as she grows.
/// </summary>
public sealed record SleepPlanDetails : EntryDetails
{
    public const int MaxAdjustments = 100;

    private SleepPlanDetails(
        IReadOnlyList<SleepPlanGoal> goals, SettlingApproach approach, TimeOnly? wakeUp, IReadOnlyList<AcceptedAdjustment> adjustments, DateOnly? stoppedOn) =>
        (Goals, Approach, WakeUp, Adjustments, StoppedOn) = (goals, approach, wakeUp, adjustments, stoppedOn);

    /// <summary>In a fixed order, without repeats.</summary>
    public IReadOnlyList<SleepPlanGoal> Goals { get; }

    public SettlingApproach Approach { get; }

    public TimeOnly? WakeUp { get; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<AcceptedAdjustment> Adjustments { get; }

    /// <summary>Null while the plan runs.</summary>
    public DateOnly? StoppedOn { get; }

    public bool IsRunning => StoppedOn is null;

    /// <summary>The answers as the planner takes them.</summary>
    public SleepPlanIntake Intake => new(Goals.ToHashSet(), Approach, WakeUp);

    public static Result<SleepPlanDetails> Create(
        IEnumerable<SleepPlanGoal> goals, SettlingApproach approach, TimeOnly? wakeUp, IEnumerable<AcceptedAdjustment>? adjustments = null,
        DateOnly? stoppedOn = null)
    {
        ArgumentNullException.ThrowIfNull(goals);
        var chosen = goals.Distinct().OrderBy(g => g).ToList();
        var accepted = (adjustments ?? []).OrderBy(a => a.On).ToList();
        if (chosen.Count == 0)
            return Result<SleepPlanDetails>.Failure(SleepPlanEntryErrors.GoalRequired);
        if (chosen.Any(g => !Enum.IsDefined(g)) || !Enum.IsDefined(approach) || accepted.Any(a => !Enum.IsDefined(a.Kind) || a.Minutes is < 0 or > 120))
            return Result<SleepPlanDetails>.Failure(SleepPlanEntryErrors.Unknown);
        if (accepted.Count > MaxAdjustments)
            return Result<SleepPlanDetails>.Failure(SleepPlanEntryErrors.TooManyAdjustments);
        return new SleepPlanDetails(chosen.AsReadOnly(), approach, wakeUp, accepted.AsReadOnly(), stoppedOn);
    }

    /// <summary>The plan with one more accepted change.</summary>
    public Result<SleepPlanDetails> Accept(AcceptedAdjustment adjustment) => Create(Goals, Approach, WakeUp, [.. Adjustments, adjustment], StoppedOn);

    /// <summary>The plan stopped on <paramref name="day"/>.</summary>
    public Result<SleepPlanDetails> Stop(DateOnly day) => Create(Goals, Approach, WakeUp, Adjustments, day);

    public bool Equals(SleepPlanDetails? other) =>
        other is not null && Approach == other.Approach && WakeUp == other.WakeUp && StoppedOn == other.StoppedOn
        && Goals.SequenceEqual(other.Goals) && Adjustments.SequenceEqual(other.Adjustments);

    public override int GetHashCode() => HashCode.Combine(Approach, WakeUp, StoppedOn, Goals.Count, Adjustments.Count);
}

public static class SleepPlanEntryErrors
{
    public const string GoalRequired = "sleepplan.goal-required";
    public const string Unknown = "sleepplan.unknown";
    public const string TooManyAdjustments = "sleepplan.too-many-adjustments";
}

/// <summary>
/// <c>sleepplan</c> (Spec 019 §7): a point, the day the plan started; one running plan per baby at a time is the app's
/// rule, not the kind's, so a stopped plan and a new one may share a day.
/// </summary>
public sealed class SleepPlanKind : EntryKind<SleepPlanDetails>
{
    public static readonly SleepPlanKind Instance = new();

    private SleepPlanKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("sleepplan");

    public override SubjectType Subject => SubjectType.Baby;

    protected override TimingRule TimingFor(SleepPlanDetails details) => TimingRule.PointOnly;

    protected override Result Validate(SleepPlanDetails details, EntryTiming timing) => Result.Success;
}
