using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Feeding;

public enum BreastSide
{
    Left,
    Right,
}

/// <summary>One stretch at one breast. Open while the timer runs on that side.</summary>
public sealed record BreastSegment(BreastSide Side, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool IsOpen => End is null;

    public TimeSpan ClosedDuration => End is { } end ? end - Start : TimeSpan.Zero;
}

/// <summary>
/// A feed at the breast, recorded as timer segments, with a duration for every side it used
/// (Spec 001 §7.2; there is no untimed breast feed).
/// </summary>
public sealed record BreastFeed : FeedingDetails
{
    private BreastFeed(IReadOnlyList<BreastSegment> segments, bool isCompleted)
    {
        Segments = segments;
        IsCompleted = isCompleted;
    }

    public IReadOnlyList<BreastSegment> Segments { get; }

    public bool IsCompleted { get; }

    public TimeSpan LeftDuration => DurationOf(BreastSide.Left);

    public TimeSpan RightDuration => DurationOf(BreastSide.Right);

    public TimeSpan TotalDuration => LeftDuration + RightDuration;

    /// <summary>The side used last: "other side" in the AI context, "· L" on the log tile.</summary>
    public BreastSide LastSide => Segments[^1].Side;

    public DateTimeOffset FirstStart => Segments[0].Start;

    /// <summary>The first start to the last known instant: the last end, or the start of an open segment.</summary>
    public TimeSpan Span => (Segments[^1].End ?? Segments[^1].Start) - FirstStart;

    /// <summary>
    /// How many parts the feed came in: runs of segments in which each starts at most
    /// <see cref="FeedingThresholds.PartGap"/> after the previous ended. A side switch is not a new part.
    /// </summary>
    public int Parts => 1 + Segments.Zip(Segments.Skip(1), (previous, next) => next.Start - (previous.End ?? next.Start))
        .Count(gap => gap > FeedingThresholds.PartGap);

    /// <summary>Starts the timer on one side.</summary>
    public static BreastFeed Start(BreastSide side, DateTimeOffset at) =>
        new([new BreastSegment(side, at, null)], isCompleted: false);

    /// <summary>
    /// The form's path: "Left 10 min, then Right 5 min, started 14:00" becomes back-to-back segments.
    /// </summary>
    public static Result<BreastFeed> FromSideDurations(DateTimeOffset start, IEnumerable<(BreastSide Side, int Minutes)> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        var segments = new List<BreastSegment>();
        var cursor = start;
        foreach (var (side, minutes) in sides)
        {
            if (minutes is < FeedingThresholds.SideMinutesMin or > FeedingThresholds.SideMinutesMax)
                return Result<BreastFeed>.Failure(FeedingErrors.BreastSideDurationInvalid);

            var end = cursor.AddMinutes(minutes);
            segments.Add(new BreastSegment(side, cursor, end));
            cursor = end;
        }

        return Create(segments, isCompleted: true);
    }

    /// <summary>Builds a feed from explicit segments, checking every rule (Spec 001 §7.2, in order).</summary>
    public static Result<BreastFeed> Create(IEnumerable<BreastSegment> segments, bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var list = segments.ToList();
        var check = Check(list, isCompleted);
        return check.IsSuccess ? new BreastFeed(list.AsReadOnly(), isCompleted) : Result<BreastFeed>.Failure(check.Error!);
    }

    public Result<BreastFeed> SwitchSide(DateTimeOffset at)
    {
        if (Segments[^1] is not { IsOpen: true } open)
            return Result<BreastFeed>.Failure(FeedingErrors.BreastNoOpenSegment);

        var other = open.Side == BreastSide.Left ? BreastSide.Right : BreastSide.Left;
        return Create([.. Segments.SkipLast(1), open with { End = at }, new BreastSegment(other, at, null)], IsCompleted);
    }

    public Result<BreastFeed> Pause(DateTimeOffset at) =>
        Segments[^1] is { IsOpen: true } open
            ? Create([.. Segments.SkipLast(1), open with { End = at }], IsCompleted)
            : Result<BreastFeed>.Failure(FeedingErrors.BreastNoOpenSegment);

    public Result<BreastFeed> Resume(BreastSide side, DateTimeOffset at) =>
        Segments[^1].IsOpen
            ? Result<BreastFeed>.Failure(FeedingErrors.BreastSegmentAlreadyOpen)
            : Create([.. Segments, new BreastSegment(side, at, null)], IsCompleted);

    /// <summary>Closes any open segment at <paramref name="at"/> and marks the feed completed.</summary>
    public Result<BreastFeed> Complete(DateTimeOffset at) =>
        Segments[^1] is { IsOpen: true } open
            ? Create([.. Segments.SkipLast(1), open with { End = at }], isCompleted: true)
            : Create(Segments, isCompleted: true);

    /// <summary>The correction path from the edit form. It keeps the completion state.</summary>
    public Result<BreastFeed> WithSegments(IEnumerable<BreastSegment> segments) => Create(segments, IsCompleted);

    public bool Equals(BreastFeed? other) =>
        other is not null && IsCompleted == other.IsCompleted && Segments.SequenceEqual(other.Segments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsCompleted);
        foreach (var segment in Segments)
            hash.Add(segment);
        return hash.ToHashCode();
    }

    internal static Result Check(IReadOnlyList<BreastSegment> segments, bool isCompleted)
    {
        if (segments.Count == 0)
            return Result.Failure(FeedingErrors.BreastNoSegments);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.End is { } end && end <= segment.Start)
                return Result.Failure(TimeRange.EndNotAfterStart);

            if (i > 0 && (segments[i - 1].End is not { } previousEnd || segment.Start < previousEnd))
            {
                // An open segment before this one is reported by the next rule, not as an overlap.
                if (segments[i - 1].IsOpen)
                    return Result.Failure(FeedingErrors.BreastOpenSegmentNotLast);
                return Result.Failure(FeedingErrors.BreastSegmentsOverlap);
            }
        }

        if (isCompleted && segments[^1].IsOpen)
            return Result.Failure(FeedingErrors.BreastCompletedWithOpenSegment);

        var span = (segments[^1].End ?? segments[^1].Start) - segments[0].Start;
        return Result.Check(span <= FeedingThresholds.BreastMaxSpan, FeedingErrors.BreastTooLong);
    }

    private TimeSpan DurationOf(BreastSide side) =>
        Segments.Where(s => s.Side == side).Aggregate(TimeSpan.Zero, (sum, s) => sum + s.ClosedDuration);
}
