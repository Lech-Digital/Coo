using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Log;

public enum ChartShape
{
    Sleep,
    Awake,
    Feed,
    Diaper,
    Pump,
}

/// <summary>One thing on the day chart, as fractions of the day, with its label for the zoomed day (Spec 009b §3).</summary>
public sealed record ChartItem(ChartShape Shape, string KindCode, double From, double To, EntryId? EntryId, string Label);

/// <summary>The day chart's content (Spec 009b), pure: the band and the zoomed day draw what this returns.</summary>
public static class DayChart
{
    /// <summary>
    /// The day's sleeps, feeds, diapers and pumps, and the awake stretches between consecutive sleeps (none before the
    /// first or after the last: the day before or after may hold the sleep that bounds them, Q6). Hidden kinds leave.
    /// </summary>
    public static IReadOnlyList<ChartItem> Build(DaySummary day, Formats formats, TimeZoneInfo zone, Func<string, bool> isShown)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(isShown);

        var entries = day.Entries.OfType<LiveEntry>().ToDictionary(e => e.Id, e => e.Entry);
        var items = new List<ChartItem>();
        foreach (var mark in day.Ribbon.Where(m => isShown(m.KindCode)))
        {
            if (!entries.TryGetValue(mark.EntryId, out var entry))
                continue;
            var time = formats.Time(entry.Timing.OccurredAt, zone);
            ChartItem? item = entry.Details switch
            {
                SleepDetails => new(ChartShape.Sleep, mark.KindCode, mark.From, mark.To, mark.EntryId, Formats.Duration(Span(day.Day, mark.From, mark.To))),
                BreastFeed when entry.Timing.IsInProgress => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Strings.Get("Entry.Running")}"),
                BreastFeed f => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Sides(f)}"),
                BottleFeed b => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(b.Total)}"),
                DiaperDetails d => new(ChartShape.Diaper, mark.KindCode, mark.From, mark.To, mark.EntryId, Formats.Contents(d.Contents)),
                PumpDetails { Yield: { } y } => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(y.Total)}"),
                PumpDetails => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, time),
                _ => null,
            };
            if (item is not null)
                items.Add(item);
        }

        items.AddRange(AwakeStretches(items, day.Day));
        return [.. items.OrderBy(i => i.From).ThenBy(i => i.Shape)];
    }

    private static IEnumerable<ChartItem> AwakeStretches(List<ChartItem> items, LocalDay day)
    {
        var sleeps = items.Where(i => i.Shape == ChartShape.Sleep).OrderBy(i => i.From).ToList();
        if (sleeps.Count < 2)
            yield break;
        var awakeFrom = sleeps[0].To;
        foreach (var next in sleeps.Skip(1))
        {
            var gap = Span(day, awakeFrom, next.From);
            if (gap >= TimeSpan.FromMinutes(1))
                yield return new ChartItem(ChartShape.Awake, "sleep", awakeFrom, next.From, null, Strings.Format("Chart.Awake", Formats.Duration(gap)));
            awakeFrom = Math.Max(awakeFrom, next.To);
        }
    }

    /// <summary>"R 30 m", or "L 10 m · R 5 m": each side used, with its time.</summary>
    private static string Sides(BreastFeed feed)
    {
        var parts = new List<string>();
        if (feed.LeftDuration > TimeSpan.Zero)
            parts.Add($"{Formats.Side(BreastSide.Left, shortForm: true)} {Formats.Duration(feed.LeftDuration)}");
        if (feed.RightDuration > TimeSpan.Zero)
            parts.Add($"{Formats.Side(BreastSide.Right, shortForm: true)} {Formats.Duration(feed.RightDuration)}");
        return string.Join(" · ", parts);
    }

    internal static TimeSpan Span(LocalDay day, double from, double to) =>
        TimeSpan.FromTicks((long)Math.Round((to - from) * day.Length.Ticks));
}
