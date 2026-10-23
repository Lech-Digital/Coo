namespace Coo.Domain.Insights;

/// <summary>One age band of the prior: the typical wake window, from the first of the day to the one before bedtime.</summary>
/// <param name="FromDays">The band starts at this corrected age in days.</param>
/// <param name="TypicalNaps">Naps on a typical day: the windows of a day are one more.</param>
public sealed record WakeWindowBand(int FromDays, TimeSpan Low, TimeSpan High, int TypicalNaps)
{
    /// <summary>
    /// The prior for window <paramref name="position"/> (1 is the morning's): the low end first, the high end before
    /// bedtime, linear in between (ADR 0008 §5).
    /// </summary>
    public TimeSpan At(int position)
    {
        var fraction = Math.Clamp((position - 1) / (double)TypicalNaps, 0, 1);
        return Low + ((High - Low) * fraction);
    }
}

/// <summary>
/// The age-typical wake windows (ADR 0008 §5), versioned data rather than numbers in code. Taken from commonly published
/// guidance; to be reviewed against current pediatric sources whenever it changes, which bumps <see cref="Version"/>.
/// </summary>
public static class WakeWindowPrior
{
    public const string Version = "prior-2026-10";

    private const double DaysPerMonth = 30.44;

    private static readonly WakeWindowBand[] Bands =
    [
        new(0, TimeSpan.FromMinutes(35), TimeSpan.FromMinutes(60), 5),
        new(4 * 7, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(90), 4),
        new(12 * 7, TimeSpan.FromMinutes(75), TimeSpan.FromMinutes(120), 4),
        new(Months(5), TimeSpan.FromHours(2), TimeSpan.FromHours(3), 3),
        new(Months(7), TimeSpan.FromHours(2.5), TimeSpan.FromHours(3.5), 2),
        new(Months(11), TimeSpan.FromHours(3), TimeSpan.FromHours(4), 2),
        new(Months(14), TimeSpan.FromHours(4), TimeSpan.FromHours(6), 1),
    ];

    /// <summary>The band for a corrected age in days.</summary>
    public static WakeWindowBand For(int correctedDays) => Bands.Last(b => b.FromDays <= Math.Max(0, correctedDays));

    private static int Months(int months) => (int)Math.Round(months * DaysPerMonth);
}
