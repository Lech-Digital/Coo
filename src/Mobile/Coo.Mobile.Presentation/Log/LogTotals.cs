using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Log;

/// <summary>
/// The two lines under each of the Log's totals (owner, 2026-11-05: "make the tiles more contentful"): what the number
/// is made of ("Breast 2 · bottle 1, 90 ml"), and how it compares ("Last 9:52 AM · usually 6"). The usual day is the
/// average of the logged days in the week before the shown one; without any, there is no comparison.
/// </summary>
public static class LogTotals
{
    /// <param name="day">The shown day's care; null when nothing was counted.</param>
    /// <param name="before">The days before it, for "usually"; days without entries are left out.</param>
    /// <param name="last">The last time of each kind on the shown day, when it is today ("feeding" → "9:52 AM").</param>
    public static (string Detail, string Compare) For(
        string kindCode, DayCare? day, IReadOnlyCollection<DayCare> before, int pumpSessions, IReadOnlyDictionary<string, string> last, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(last);
        ArgumentNullException.ThrowIfNull(formats);
        var logged = before.Where(d => d.HasEntries).ToList();
        var pumpedMl = logged.Count == 0 ? 0m : Math.Round(logged.Average(d => d.PumpedMl));
        string? usual = logged.Count == 0 ? null : kindCode switch
        {
            "feeding" => Math.Round(logged.Average(d => d.Feeds)).ToString("0", Strings.Culture),
            "diaper" => Math.Round(logged.Average(d => d.Diapers)).ToString("0", Strings.Culture),
            "sleep" => Formats.Duration(TimeSpan.FromTicks((long)logged.Average(d => d.Sleep.Ticks))),
            "pump" when pumpedMl > 0 => formats.Volume(Coo.Domain.CareLog.Volume.FromMillilitres(pumpedMl).Value),
            _ => null,
        };

        var detail = day is null ? "" : kindCode switch
        {
            "feeding" when day.Feeds > 0 => (day.BreastFeeds, day.Bottles) switch
            {
                ( > 0, > 0) => Strings.Format("Totals.FeedSplit", day.BreastFeeds, day.Bottles),
                ( > 0, _) => Strings.Format("Totals.BreastOnly", day.BreastFeeds),
                (_, > 0) => Strings.Format("Totals.BottleOnly", day.Bottles),
                _ => "",
            } + (day.BottleMl > 0 ? ", " + formats.Volume(Coo.Domain.CareLog.Volume.FromMillilitres(day.BottleMl).Value) : ""),
            "diaper" when day.Diapers > 0 => Strings.Format("Totals.DiaperSplit", day.Wet, day.Dirty),
            "sleep" when day.Sleeps > 0 => Strings.Format("Totals.Longest", Formats.Duration(day.LongestSleep)),
            "pump" when pumpSessions > 0 => Strings.Format("Totals.Sessions", pumpSessions),
            _ => "",
        };

        var lastAt = last.GetValueOrDefault(kindCode);
        var compare = (lastAt, usual) switch
        {
            ({ } l, { } u) => Strings.Format("Totals.LastAndUsually", l, u),
            ({ } l, null) => Strings.Format("Totals.Last", l),
            (null, { } u) => Strings.Format("Totals.Usually", u),
            _ => "",
        };
        return (detail, compare);
    }
}
