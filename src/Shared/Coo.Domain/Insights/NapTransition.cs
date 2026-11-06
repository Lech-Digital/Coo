namespace Coo.Domain.Insights;

/// <summary>A change in how many naps she takes: settled (<paramref name="Settled"/>) or on its way.</summary>
/// <param name="From">Naps a day the week before.</param>
/// <param name="To">Naps a day now.</param>
/// <param name="Settled">Most days this week already have the new count.</param>
public sealed record NapTransition(int From, int To, bool Settled);

/// <summary>
/// Nap transitions (4 naps to 3, 3 to 2, 2 to 1; owner, 2026-11-06): in the days around one, the last nap shortens or is
/// skipped and wake windows shift, so predictions get worse for a week without saying why. This names it: from the naps
/// of each full day in the last fortnight, this week against the one before.
/// </summary>
public static class NapTransitions
{
    /// <summary>Full days with a night logged, in each week, before the weeks are compared.</summary>
    public const int DaysPerWeek = 4;

    /// <param name="sleeps">The sleeps of at least the last 15 days, any order.</param>
    public static NapTransition? Detect(IEnumerable<SleepSpan> sleeps, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        var recent = sleeps.Where(s => s.End is not null && s.End <= now && s.Start >= now - TimeSpan.FromDays(16)).OrderBy(s => s.Start).ToList();
        if (recent.Count == 0)
            return null;
        var classes = SleepClassifier.Classify(recent, SleepClassifier.LearnNightWindow(recent, zone), zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // Naps per full day; a day counts once a night began on it, so a day nobody logged is not a day without naps.
        var naps = new Dictionary<DateOnly, int>();
        for (var i = 0; i < recent.Count; i++)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(recent[i].Start, zone).DateTime);
            if (day >= today || day < today.AddDays(-14))
                continue;
            if (classes[i] == SleepClass.Night && SleepClassifier.Local(recent[i].Start, zone).Hour >= 12)
                naps.TryAdd(day, 0);
        }

        for (var i = 0; i < recent.Count; i++)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(recent[i].Start, zone).DateTime);
            if (classes[i] == SleepClass.Nap && naps.TryGetValue(day, out var count))
                naps[day] = count + 1;
        }

        var thisWeek = naps.Where(n => n.Key >= today.AddDays(-7)).Select(n => n.Value).ToList();
        var lastWeek = naps.Where(n => n.Key < today.AddDays(-7)).Select(n => n.Value).ToList();
        if (thisWeek.Count < DaysPerWeek || lastWeek.Count < DaysPerWeek)
            return null;

        var before = Mode(lastWeek);
        var fewer = thisWeek.Count(n => n < before);
        if (before <= 1 || fewer < 2)
            return null;
        var to = Mode([.. thisWeek.Where(n => n < before)]);
        return new NapTransition(before, to, Settled: fewer * 2 > thisWeek.Count);
    }

    // The most common count; the higher one on a tie.
    private static int Mode(IReadOnlyList<int> values) => values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
}
