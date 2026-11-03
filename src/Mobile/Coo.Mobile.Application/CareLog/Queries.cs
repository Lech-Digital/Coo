using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// What Today shows for one baby (Spec 006 §4). Read-only and quarantined entries appear in the timeline only.
/// <see cref="SleepSoFar"/> is today's sleep cut at midnight, as the Log counts it: last night's part after midnight in,
/// tonight's part after midnight out.
/// </summary>
public sealed record TodaySummary(
    IReadOnlyList<CareEntry> Running,
    CareEntry? LastFeed,
    CareEntry? LastDiaper,
    CareEntry? LastSleep,
    CareEntry? LastPump,
    DateTimeOffset? AwakeSince,
    IReadOnlyList<StoredEntry> TodayEntries,
    LocalDay Today,
    TimeSpan? SleepSoFar = null,
    CareEntry? LastBath = null,
    IReadOnlyList<CareEntry>? Medicines = null,
    TimeSpan? TummyToday = null,
    CareEntry? LastBottle = null);

/// <summary>
/// One mark on the day chart, as fractions of the day (Spec 006 §7, Spec 009b §2): a sleep block, or a feed, diaper or
/// pump. A session runs to its end (a running one to now, or the day's end); a point has <c>To == From</c>.
/// </summary>
public sealed record RibbonMark(string KindCode, double From, double To, bool IsBlock, EntryId EntryId);

public sealed record DayTotals(int Feeds, int Diapers, TimeSpan Sleep, decimal PumpedMl);

/// <summary>What the Log screen shows for one day.</summary>
public sealed record DaySummary(LocalDay Day, IReadOnlyList<StoredEntry> Entries, DayTotals Totals, IReadOnlyList<RibbonMark> Ribbon);

/// <summary>The data for Today: running sessions, the last of each kind, awake-since, and today's timeline.</summary>
public sealed class GetToday(ICareLogStore store)
{
    /// <summary>How far back "last feed" and friends look.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(7);

    /// <summary>How far back the medicines given are listed, for the medicine reminders: a dose already given is not asked for.</summary>
    public static readonly TimeSpan MedicineLookBack = TimeSpan.FromDays(2);

    public async Task<TodaySummary> ExecuteAsync(
        HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DayMath.Day(DayMath.DateOf(now, zone), zone);
        var stored = await store.ListAsync(household, now - LookBack, now.AddDays(1), cancellationToken);
        var mine = stored.Where(e => e is not LiveEntry live || IsAbout(live.Entry, baby)).ToList();
        var live = mine.OfType<LiveEntry>().Select(l => l.Entry).ToList();

        var lastSleep = Latest<SleepDetails>(live);
        DateTimeOffset? awakeSince = lastSleep?.Timing is SessionTiming { Range.End: { } woke } ? woke : null;

        return new TodaySummary(
            live.Where(e => e.Timing.IsInProgress).OrderBy(e => e.Timing.OccurredAt).ToList(),
            Latest<FeedingDetails>(live),
            Latest<DiaperDetails>(live),
            lastSleep,
            Latest<PumpDetails>(live),
            awakeSince,
            mine.Where(e => today.Contains(e.Start)).ToList(),
            today,
            TimeSpan.FromTicks(live
                .Where(e => e.Details is SleepDetails)
                .Select(e => e.Timing is SessionTiming s ? today.Overlap(s.Range.Start, s.Range.End ?? now) : TimeSpan.Zero)
                .Sum(t => t.Ticks)),
            Latest<Coo.Domain.CareLog.Kinds.Bath.BathDetails>(live),
            live.Where(e => e.Details is Coo.Domain.CareLog.Kinds.Health.MedicineDetails && e.Timing.OccurredAt >= now - MedicineLookBack)
                .OrderBy(e => e.Timing.OccurredAt).ToList(),
            TimeSpan.FromTicks(live
                .Where(e => e.Details is Coo.Domain.CareLog.Kinds.Development.TummyDetails)
                .Select(e => e.Timing is SessionTiming s ? today.Overlap(s.Range.Start, s.Range.End ?? now) : TimeSpan.Zero)
                .Sum(t => t.Ticks)),
            live.Where(e => e.Details is BottleFeed).MaxBy(e => e.Timing.OccurredAt));
    }

    internal static bool IsAbout(CareEntry entry, BabyId baby) =>
        entry.Subject is BabySubject { BabyId: var id } ? id == baby : entry.Subject is CaregiverSubject;

    private static CareEntry? Latest<TDetails>(IEnumerable<CareEntry> entries)
        where TDetails : EntryDetails =>
        entries.Where(e => e.Details is TDetails).MaxBy(e => e.Timing.OccurredAt);
}

/// <summary>One local day for the Log screen: entries, totals and the ribbon. Sleeps across midnight are cut there.</summary>
public sealed class GetDay(ICareLogStore store)
{
    public async Task<DaySummary> ExecuteAsync(
        HouseholdId household, BabyId baby, DateOnly date, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = DayMath.Day(date, zone);

        // From a day earlier, so a sleep that started yesterday evening is found.
        var stored = await store.ListAsync(household, day.Start.AddDays(-1), day.End, cancellationToken);
        var mine = stored.Where(e => e is not LiveEntry live || GetToday.IsAbout(live.Entry, baby)).ToList();

        var inDay = new List<StoredEntry>();
        var ribbon = new List<RibbonMark>();
        int feeds = 0, diapers = 0;
        var sleep = TimeSpan.Zero;
        var pumped = 0m;

        foreach (var stored1 in mine)
        {
            if (stored1 is not LiveEntry { Entry: var entry })
            {
                if (day.Contains(stored1.Start))
                    inDay.Add(stored1);
                continue;
            }

            var (start, end) = entry.Timing switch
            {
                SessionTiming s => (s.Range.Start, s.Range.End ?? (now < day.End ? now : day.End)),
                PointTiming p => (p.At, p.At),
                _ => (entry.Timing.OccurredAt, entry.Timing.OccurredAt),
            };

            var touchesDay = day.Contains(start) || (end > day.Start && start < day.End);
            if (!touchesDay)
                continue;

            inDay.Add(stored1);
            switch (entry.Details)
            {
                case SleepDetails:
                    sleep += day.Overlap(start, end);
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: true, entry.Id));
                    break;
                case FeedingDetails:
                    feeds++;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: false, entry.Id));
                    break;
                case DiaperDetails when day.Contains(start):
                    diapers++;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(start), IsBlock: false, entry.Id));
                    break;
                case PumpDetails pump when day.Contains(start):
                    pumped += pump.Yield?.Total.Millilitres ?? 0m;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: false, entry.Id));
                    break;
            }
        }

        return new DaySummary(day, inDay.OrderByDescending(e => e.Start).ToList(), new DayTotals(feeds, diapers, sleep, pumped), ribbon);
    }
}

/// <summary>A medicine that is a vitamin or supplement, by its name in any of Coo's languages ("Vitamin D", "witamina D3", "D-Fluoretten").</summary>
public static class Vitamins
{
    private static readonly string[] Words = ["vitamin", "vit ", "vit.", "witamin", "vitamine", "vitamina", "d3", "d-vit", "fluoretten", "devit", "iron", "eisen", "fer ", "żelazo", "ijzer", "hierro", "ferro", "probiotic", "probiotyk"];

    public static bool IsVitamin(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = " " + name.ToLowerInvariant() + " ";
        return Words.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }
}
