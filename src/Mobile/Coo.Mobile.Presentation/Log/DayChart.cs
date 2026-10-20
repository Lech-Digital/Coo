using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
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

/// <summary>A stretch of the day, as fractions: a quiet-hours wash.</summary>
public sealed record ChartBand(double From, double To);

/// <summary>An hour boundary: where it falls, the local hour (24 for the day's end), and its label ("13:00", "1:00 PM").</summary>
public sealed record HourTick(double At, int Hour, string Label);

/// <summary>The day chart's content (Spec 009b), pure: the band and the zoomed day draw what this returns.</summary>
public static class DayChart
{
    /// <summary>
    /// The day's sleeps, feeds, diapers and pumps, and the awake stretches between consecutive sleeps (none before the
    /// first or after the last: the day before or after may hold the sleep that bounds them, Q6). Hidden kinds leave.
    /// </summary>
    public static IReadOnlyList<ChartItem> Build(DaySummary day, Formats formats, TimeZoneInfo zone, Func<string, bool> isShown, DateTimeOffset? now = null)
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
                SleepDetails => new(
                    ChartShape.Sleep,
                    mark.KindCode,
                    mark.From,
                    mark.To,
                    mark.EntryId,
                    entry.Timing is SessionTiming s
                        ? Formats.Duration(s.Range.Duration(now ?? day.Day.End))
                        : Formats.Duration(Span(day.Day, mark.From, mark.To))),
                BreastFeed when entry.Timing.IsInProgress => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Strings.Get("Entry.Running")}"),
                BreastFeed { Parts: > 1 } f => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Sides(f)} · {Formats.Parts(f)}"),
                BreastFeed f => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Sides(f)}"),
                BottleFeed b => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(b.Total)}"),
                DiaperDetails d => new(ChartShape.Diaper, mark.KindCode, mark.From, mark.To, mark.EntryId, Formats.Contents(d.Contents)),
                PumpDetails { Yield: { } y } => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(y.Total)}"),
                PumpDetails => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, time),
                _ => null,
            };
            if (item is null)
                continue;
            if (entry.Details is BreastFeed { IsCompleted: true, Parts: > 1 } parted)
                items.AddRange(Parts(item, parted, day.Day));
            else
                items.Add(item);
        }

        items.AddRange(AwakeStretches(items, day.Day));
        return [.. items.OrderBy(i => i.From).ThenBy(i => i.Shape)];
    }

    /// <summary>
    /// A feed in parts as its parts (Spec 011d §5): each a bar of its real length, the gaps empty, so the chart shows the
    /// pauses. The feed is labelled once, at its first part; a tap on any part opens it. A feed is charted on its start
    /// day only, and a part past the day's end is cut there, as a bar across midnight always was: a part that starts
    /// after midnight is clamped to the edge.
    /// </summary>
    private static IEnumerable<ChartItem> Parts(ChartItem feed, BreastFeed parted, LocalDay day)
    {
        var first = true;
        foreach (var part in FeedParts.Of(parted))
        {
            yield return feed with
            {
                From = day.Fraction(part[0].Start),
                To = day.Fraction(part[^1].End ?? part[^1].Start),
                Label = first ? feed.Label : "",
            };
            first = false;
        }
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

    /// <summary>Quiet hours as washes behind the band (Spec 009b §2): one band, two across midnight, none when start equals end.</summary>
    public static IReadOnlyList<ChartBand> QuietBands(QuietHours quiet, LocalDay day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(zone);
        if (quiet.Start == quiet.End)
            return [];

        double At(TimeOnly time)
        {
            var local = day.Date.ToDateTime(time);
            return day.Fraction(new DateTimeOffset(local, zone.GetUtcOffset(local)));
        }

        return quiet.Start < quiet.End
            ? [new ChartBand(At(quiet.Start), At(quiet.End))]
            : [new ChartBand(0, At(quiet.End)), new ChartBand(At(quiet.Start), 1)];
    }

    /// <summary>Every hour boundary of the day, in the household zone: 25 on most days, 24 or 26 at the clock changes.</summary>
    public static IReadOnlyList<HourTick> Hours(LocalDay day, TimeZoneInfo zone, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        var ticks = new List<HourTick>();
        for (var instant = day.Start; instant <= day.End; instant = instant.AddHours(1))
        {
            var hour = instant == day.End ? 24 : TimeZoneInfo.ConvertTime(instant, zone).Hour;
            ticks.Add(new HourTick(day.Fraction(instant), hour, formats.Time(instant, zone)));
        }

        return ticks;
    }

    /// <summary>The zoomed day's title (Spec 009b §3): "Today, 12:30 – 18:30", "Wed 30 Sep, 03:00 – 09:00".</summary>
    public static string RangeTitle(LocalDay day, bool isToday, double from, double to, Formats formats, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        DateTimeOffset At(double fraction)
        {
            var instant = day.Start + Span(day, 0, fraction);
            var fiveMinutes = TimeSpan.FromMinutes(5).Ticks;
            return new DateTimeOffset((instant.UtcTicks + fiveMinutes / 2) / fiveMinutes * fiveMinutes, TimeSpan.Zero);
        }

        var name = isToday ? Strings.Get("Section.Today") : day.Date.ToString("ddd d MMM", Strings.Culture);
        return Strings.Format("Chart.Range", name, formats.Time(At(from), zone), formats.Time(At(to), zone));
    }

    /// <summary>
    /// The item under a tap in the zoomed day (Spec 009b §3): a feed, diaper or pump within the tolerance first, the
    /// nearest, then the shortest; a sleep only when none is near, so a bottle or a pump during a nap opens itself
    /// (reviewer's finding). Awake stretches are not entries and are never hit.
    /// </summary>
    public static ChartItem? HitTest(IReadOnlyList<ChartItem> items, double at, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(i => i.EntryId is not null)
            .Select(i => (Item: i, Distance: at < i.From ? i.From - at : at > i.To ? at - i.To : 0))
            .Where(c => c.Distance <= tolerance)
            .OrderBy(c => c.Item.Shape == ChartShape.Sleep)
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Item.To - c.Item.From)
            .Select(c => c.Item)
            .FirstOrDefault();
    }

    /// <summary>What a screen reader says for the band (Spec 009b §2): "Sleep 14 h 5 m; Feeds 7; Diapers 7; Pumping 2".</summary>
    public static string Summary(IReadOnlyList<ChartItem> items, LocalDay day, Func<string, bool> isShown)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isShown);
        var parts = new List<string>();
        if (isShown("sleep"))
        {
            var sleep = items.Where(i => i.Shape == ChartShape.Sleep).Aggregate(TimeSpan.Zero, (sum, i) => sum + Span(day, i.From, i.To));
            parts.Add(Strings.Format("Chart.SummarySleep", Formats.Duration(sleep)));
        }

        if (isShown("feeding"))
            parts.Add(Strings.Format("Chart.SummaryFeeds", items.Where(i => i.Shape == ChartShape.Feed).Select(i => i.EntryId).Distinct().Count()));
        if (isShown("diaper"))
            parts.Add(Strings.Format("Chart.SummaryDiapers", items.Count(i => i.Shape == ChartShape.Diaper)));
        if (isShown("pump"))
            parts.Add(Strings.Format("Chart.SummaryPumps", items.Count(i => i.Shape == ChartShape.Pump)));
        return string.Join("; ", parts);
    }

    /// <summary>Where the zoomed day centres a tap: the nearest whole hour (owner, 2026-10-02), the tap itself if there are no hours.</summary>
    public static double NearestHour(IReadOnlyList<HourTick> hours, double at)
    {
        ArgumentNullException.ThrowIfNull(hours);
        return hours.Count == 0 ? at : hours.MinBy(h => Math.Abs(h.At - at))!.At;
    }

    internal static TimeSpan Span(LocalDay day, double from, double to) =>
        TimeSpan.FromTicks((long)Math.Round((to - from) * day.Length.Ticks));
}
