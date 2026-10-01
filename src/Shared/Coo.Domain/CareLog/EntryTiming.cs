namespace Coo.Domain.CareLog;

/// <summary>When an entry happened: a point in time, or a session (Spec 001 §4.3).</summary>
public abstract record EntryTiming
{
    private protected EntryTiming()
    {
    }

    /// <summary>Orders the timeline and drives the duplicate check.</summary>
    public abstract DateTimeOffset OccurredAt { get; }

    public abstract bool IsInProgress { get; }

    /// <summary>Every instant the timing holds, for rules that apply to all of them.</summary>
    public abstract IEnumerable<DateTimeOffset> Instants { get; }

    public static EntryTiming Point(DateTimeOffset at) => new PointTiming(at);

    public static EntryTiming Session(TimeRange range) => new SessionTiming(range);
}

public sealed record PointTiming(DateTimeOffset At) : EntryTiming
{
    public override DateTimeOffset OccurredAt => At;

    public override bool IsInProgress => false;

    public override IEnumerable<DateTimeOffset> Instants => [At];
}

public sealed record SessionTiming(TimeRange Range) : EntryTiming
{
    public override DateTimeOffset OccurredAt => Range.Start;

    public override bool IsInProgress => Range.IsOpen;

    public override IEnumerable<DateTimeOffset> Instants =>
        Range.End is { } end ? [Range.Start, end] : [Range.Start];
}

/// <summary>Which shape of timing a kind accepts for given details (Spec 001 §7.1).</summary>
public enum TimingRule
{
    PointOnly,
    SessionOnly,

    /// <summary>The kind computes the timing from its details; callers never pass it.</summary>
    DerivedFromDetails,
}
