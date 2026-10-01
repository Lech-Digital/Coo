using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>A start, and an end once finished. An open range is in progress (Spec 001 §4.2).</summary>
public sealed record TimeRange
{
    public const string EndNotAfterStart = "time.end-not-after-start";

    private TimeRange(DateTimeOffset start, DateTimeOffset? end)
    {
        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset? End { get; }

    public bool IsOpen => End is null;

    public static Result<TimeRange> Create(DateTimeOffset start, DateTimeOffset? end = null) =>
        end is { } e && e <= start ? Result<TimeRange>.Failure(EndNotAfterStart) : new TimeRange(start, end);

    public static TimeRange Open(DateTimeOffset start) => new(start, null);

    public TimeSpan Duration(DateTimeOffset now) => (End ?? now) - Start;

    public bool Overlaps(TimeRange other, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < (other.End ?? now) && other.Start < (End ?? now);
    }
}
