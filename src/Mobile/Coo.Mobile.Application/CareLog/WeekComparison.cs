namespace Coo.Mobile.Application.CareLog;

/// <summary>This week against the one before, per logged day (owner, 2026-11-06), for Today's note and the Advisor.</summary>
/// <param name="Sleep">Change in sleep a day.</param>
/// <param name="Longest">Change in the day's longest sleep.</param>
/// <param name="Feeds">Change in feeds a day.</param>
/// <param name="WetPerDay">Wet diapers a day this week.</param>
/// <param name="Compared">Both weeks had enough logged days to compare; the differences are zero otherwise.</param>
public sealed record WeekComparison(TimeSpan Sleep, TimeSpan Longest, double Feeds, double WetPerDay, bool HasThisWeek, bool Compared)
{
    /// <summary>Logged days needed in a week.</summary>
    public const int DaysNeeded = 4;

    public static WeekComparison Of(IReadOnlyList<DayCare> thisWeek, IReadOnlyList<DayCare> lastWeek)
    {
        ArgumentNullException.ThrowIfNull(thisWeek);
        ArgumentNullException.ThrowIfNull(lastWeek);
        var now = thisWeek.Where(d => d.HasEntries).ToList();
        var before = lastWeek.Where(d => d.HasEntries).ToList();
        if (now.Count < DaysNeeded)
            return new WeekComparison(TimeSpan.Zero, TimeSpan.Zero, 0, 0, false, false);

        // Wet diapers only from days with any diaper logged: a day without diapers logged is not a dry day.
        var diaperDays = now.Where(d => d.Diapers > 0).ToList();
        var wet = diaperDays.Count == 0 ? double.NaN : diaperDays.Average(d => d.Wet);
        if (before.Count < DaysNeeded)
            return new WeekComparison(TimeSpan.Zero, TimeSpan.Zero, 0, wet, true, false);

        return new WeekComparison(
            TimeSpan.FromMinutes(now.Average(d => d.Sleep.TotalMinutes) - before.Average(d => d.Sleep.TotalMinutes)),
            TimeSpan.FromMinutes(now.Average(d => d.LongestSleep.TotalMinutes) - before.Average(d => d.LongestSleep.TotalMinutes)),
            now.Average(d => d.Feeds) - before.Average(d => d.Feeds),
            wet,
            true,
            true);
    }
}
