using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Time;

/// <summary>The model's structured time (ADR 0005 §5). The model does no arithmetic; this resolver does.</summary>
public abstract record TimeExpression
{
    private protected TimeExpression()
    {
    }

    public static TimeExpression Now { get; } = new NowExpression();

    public static TimeExpression Unspecified { get; } = new UnspecifiedExpression();

    public static TimeExpression MinutesAgo(int minutes) => new MinutesAgoExpression(minutes);

    public static TimeExpression Clock(int hour, int minute) => new ClockExpression(hour, minute);
}

public sealed record NowExpression : TimeExpression;

public sealed record UnspecifiedExpression : TimeExpression;

public sealed record MinutesAgoExpression(int Minutes) : TimeExpression;

public sealed record ClockExpression(int Hour, int Minute) : TimeExpression;

/// <summary>A resolved instant, and a warning when the time was not actually stated.</summary>
public sealed record ResolvedTime(DateTimeOffset At, Warning? Warning = null);

/// <summary>Resolves a time expression against when the words were captured (Spec 001 §10).</summary>
public static class TimeExpressionResolver
{
    public const string MinutesAgoOutOfRange = "time.minutes-ago-out-of-range";
    public const string ClockInvalid = "time.clock-invalid";
    public const int MaxMinutesAgo = 2880;

    /// <summary>"14:00" said at 13:58 still means today's 14:00.</summary>
    public static readonly TimeSpan ClockGrace = TimeSpan.FromMinutes(5);

    public static Result<ResolvedTime> Resolve(TimeExpression expression, DateTimeOffset capturedAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(zone);

        return expression switch
        {
            NowExpression => new ResolvedTime(capturedAt),
            UnspecifiedExpression => new ResolvedTime(capturedAt, PlausibilityCodes.TimeNotStated),
            MinutesAgoExpression { Minutes: >= 0 and <= MaxMinutesAgo } ago => new ResolvedTime(capturedAt.AddMinutes(-ago.Minutes)),
            MinutesAgoExpression => Result<ResolvedTime>.Failure(MinutesAgoOutOfRange),
            ClockExpression { Hour: >= 0 and <= 23, Minute: >= 0 and <= 59 } clock => ResolveClock(clock, capturedAt, zone),
            ClockExpression => Result<ResolvedTime>.Failure(ClockInvalid),
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name, "Unknown time expression."),
        };
    }

    private static ResolvedTime ResolveClock(ClockExpression clock, DateTimeOffset capturedAt, TimeZoneInfo zone)
    {
        var limit = capturedAt + ClockGrace;
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(capturedAt, zone).DateTime);

        // The latest local hh:mm that is no later than the limit: tomorrow (just after midnight), today, or yesterday.
        var best = new[] { 1, 0, -1, -2 }
            .SelectMany(offset => InstantsOf(localDate.AddDays(offset).ToDateTime(new TimeOnly(clock.Hour, clock.Minute)), zone))
            .Where(instant => instant <= limit)
            .Max();

        return new ResolvedTime(TimeZoneInfo.ConvertTime(best, zone));
    }

    /// <summary>Every instant that a local wall-clock time names in the zone: none is lost, both are kept.</summary>
    private static IEnumerable<DateTimeOffset> InstantsOf(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(local))
            return zone.GetAmbiguousTimeOffsets(local).Select(offset => new DateTimeOffset(local, offset));

        if (zone.IsInvalidTime(local))
        {
            // A skipped time (spring forward) moves forward by the gap: read it with the offset in force before
            // the gap. 02:30 in Europe/Warsaw on the spring-forward day becomes 03:30 local.
            var before = zone.GetUtcOffset(local.AddHours(-3));
            return [new DateTimeOffset(local, before)];
        }

        return [new DateTimeOffset(local, zone.GetUtcOffset(local))];
    }
}
