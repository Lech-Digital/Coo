namespace Coo.Mobile.Application.CareLog;

/// <summary>A local calendar day as an instant window: 23, 24 or 25 hours long across DST changes (Spec 006 §7).</summary>
public sealed record LocalDay(DateOnly Date, DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;

    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>The part of [start, end) inside this day.</summary>
    public TimeSpan Overlap(DateTimeOffset start, DateTimeOffset end)
    {
        var from = start > Start ? start : Start;
        var to = end < End ? end : End;
        return to > from ? to - from : TimeSpan.Zero;
    }

    /// <summary>Where an instant falls in the day, from 0 (midnight) to 1 (the next midnight), clamped.</summary>
    public double Fraction(DateTimeOffset instant) =>
        Math.Clamp((instant - Start).TotalMilliseconds / Length.TotalMilliseconds, 0, 1);
}

/// <summary>Pure day arithmetic in a time zone (Spec 006 §8).</summary>
public static class DayMath
{
    public static LocalDay Day(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new LocalDay(date, StartOf(date, zone), StartOf(date.AddDays(1), zone));
    }

    public static DateOnly DateOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    /// <summary>Local midnight. Where midnight itself is skipped by DST, the first valid instant after it.</summary>
    private static DateTimeOffset StartOf(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
