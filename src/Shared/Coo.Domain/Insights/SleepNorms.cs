namespace Coo.Domain.Insights;

/// <summary>
/// What is typical for a baby of an age band (Spec 019 §4): naps a day, a nap's length, the night, sleep in 24 hours,
/// and the bedtime range once there is one. General guidance from mainstream sources (AAP, NHS, commonly published
/// schedules), versioned data rather than numbers in code; reviewed when it changes, which bumps <see cref="SleepNorms.Version"/>.
/// </summary>
/// <param name="FromDays">The band starts at this corrected age in days.</param>
/// <param name="Naps">Naps on a typical day.</param>
/// <param name="FewestNaps">The fewest naps still typical: a baby dropping a nap is in range down to here.</param>
/// <param name="NapLength">A typical nap.</param>
/// <param name="Night">The night's sleep, wakings left out.</param>
/// <param name="TotalLow">The least sleep in 24 hours still typical.</param>
/// <param name="TotalHigh">The most.</param>
/// <param name="BedtimeEarliest">The earliest usual bedtime; null while there is no set bedtime yet (the first weeks).</param>
/// <param name="BedtimeLatest">The latest usual bedtime.</param>
public sealed record SleepNormBand(
    int FromDays,
    int Naps,
    int FewestNaps,
    TimeSpan NapLength,
    TimeSpan Night,
    TimeSpan TotalLow,
    TimeSpan TotalHigh,
    TimeOnly? BedtimeEarliest,
    TimeOnly? BedtimeLatest)
{
    /// <summary>A set bedtime is part of the plan from here (about 3 months); before, the day ends when the last window does.</summary>
    public bool HasBedtime => BedtimeEarliest is not null && BedtimeLatest is not null;
}

public static class SleepNorms
{
    public const string Version = "norms-2026-11";

    private const double DaysPerMonth = 30.44;

    private static readonly SleepNormBand[] Bands =
    [
        new(0, 5, 4, TimeSpan.FromMinutes(45), TimeSpan.FromHours(8), TimeSpan.FromHours(14), TimeSpan.FromHours(17), null, null),
        new(4 * 7, 4, 4, TimeSpan.FromMinutes(60), TimeSpan.FromHours(9), TimeSpan.FromHours(14), TimeSpan.FromHours(17), null, null),
        new(12 * 7, 4, 3, TimeSpan.FromMinutes(60), TimeSpan.FromHours(10), TimeSpan.FromHours(12), TimeSpan.FromHours(16), new TimeOnly(19, 0), new TimeOnly(21, 0)),
        new(Months(5), 3, 3, TimeSpan.FromMinutes(70), TimeSpan.FromHours(11), TimeSpan.FromHours(12), TimeSpan.FromHours(16), new TimeOnly(18, 30), new TimeOnly(20, 0)),
        new(Months(7), 2, 2, TimeSpan.FromMinutes(80), TimeSpan.FromHours(11), TimeSpan.FromHours(12), TimeSpan.FromHours(16), new TimeOnly(18, 30), new TimeOnly(20, 0)),
        new(Months(11), 2, 1, TimeSpan.FromMinutes(75), TimeSpan.FromHours(11), TimeSpan.FromHours(11), TimeSpan.FromHours(14), new TimeOnly(18, 30), new TimeOnly(20, 0)),
        new(Months(14), 1, 1, TimeSpan.FromMinutes(120), TimeSpan.FromHours(11), TimeSpan.FromHours(11), TimeSpan.FromHours(14), new TimeOnly(19, 0), new TimeOnly(20, 30)),
    ];

    /// <summary>The band for a corrected age in days.</summary>
    public static SleepNormBand For(int correctedDays) => Bands.Last(b => b.FromDays <= Math.Max(0, correctedDays));

    private static int Months(int months) => (int)Math.Round(months * DaysPerMonth);
}
