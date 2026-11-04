using System.Globalization;
using System.Text;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Development;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Growth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Advisor;

/// <summary>
/// The baby's recent log as plain English for the Advisor (sent with each question): what is happening now, the last
/// of each kind, every entry of the last two days, each of the last fourteen days, the last four weeks, the next likely
/// sleep, health and growth. Facts only, in the family's time zone and without notes, so the answer can quote them
/// instead of guessing (owner, 2026-10-24: "more than a few logs back").
/// </summary>
public sealed class BuildAdvisorContext(
    GetToday today, GetCareTrends trends, ICareLogStore? store = null, IHouseholdStore? households = null, IGrowthChartPreference? chart = null,
    Insights.GetSleepPrediction? sleep = null)
{
    /// <summary>Entry by entry this far back; before that, by day.</summary>
    public static readonly TimeSpan DetailWindow = TimeSpan.FromHours(48);

    /// <summary>Days told one by one, today included.</summary>
    public const int Days = 14;

    /// <summary>At most this many entries in the two-day list, the newest kept: a log of twin pumping can be long.</summary>
    public const int MaxDetailLines = 120;

    /// <summary>How far back medicines, temperatures, vaccines and visits are told.</summary>
    public static readonly TimeSpan HealthWindow = TimeSpan.FromDays(14);

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public async Task<string> ExecuteAsync(
        HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var summary = await today.ExecuteAsync(household, baby, zone, now, cancellationToken);
        var date = DayMath.DateOf(now, zone);
        var days = await trends.DaysAsync(household, baby, date.AddDays(1 - Days), date, zone, now, cancellationToken);
        var weeks = await trends.ExecuteAsync(household, baby, TrendGrain.Week, date, 4, DayOfWeek.Monday, zone, now, cancellationToken);
        string At(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).ToString("ddd HH:mm", En);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Now: {TimeZoneInfo.ConvertTime(now, zone).ToString("dddd d MMMM yyyy, HH:mm", En)} ({zone.Id}).");

        foreach (var running in summary.Running)
            text.AppendLine(CultureInfo.InvariantCulture, $"In progress: {Describe(running, now)} since {At(running.Timing.OccurredAt)}.");
        if (summary.AwakeSince is { } awake && !summary.Running.Any(e => e.Details is SleepDetails))
            text.AppendLine(CultureInfo.InvariantCulture, $"Awake since {At(awake)} ({Duration(now - awake)}).");

        foreach (var last in new[] { summary.LastFeed, summary.LastSleep, summary.LastDiaper, summary.LastPump })
        {
            if (last is not null && !last.Timing.IsInProgress)
                text.AppendLine(CultureInfo.InvariantCulture, $"Last {last.KindCode.Value}: {At(last.Timing.OccurredAt)}, {Describe(last, now)}.");
        }

        if (sleep is not null && households is not null
            && (await households.ListBabiesAsync(household, cancellationToken)).FirstOrDefault(b => b.Id == baby) is { } profile
            && await sleep.ExecuteAsync(household, profile, zone, now, cancellationToken) is { } next)
        {
            var what = next.IsBedtime ? "Bedtime" : $"Nap {next.Position}";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Next sleep (Coo's estimate, {next.Confidence.ToString().ToLowerInvariant()} confidence): {what} likely {At(next.Earliest)}–{At(next.Latest)}; typical wake window at this age {Duration(next.Prior.Low)}–{Duration(next.Prior.High)}{(next.PersonalMedian is { } median ? $", this baby's lately {Duration(median)}" : "")}.");
        }

        if (store is not null)
            await AppendRecentEntriesAsync(text, household, baby, zone, now, cancellationToken);

        text.AppendLine(CultureInfo.InvariantCulture, $"Per day, last {Days} days (sleep is cut at midnight; today is not over):");
        foreach (var day in days.Values.OrderBy(d => d.Date))
            text.AppendLine(Line(day, day.Date == date));

        if (weeks.Count > 0)
        {
            text.AppendLine("Per week (Monday to Sunday), daily averages:");
            for (var i = weeks.Count - 1; i >= 0; i--)
                text.AppendLine(Week(i == weeks.Count - 1 ? "This week so far" : $"Week of {weeks[i].Start.ToString("d MMM", En)}", weeks[i]));
        }

        if (store is not null)
            await AppendHealthAndGrowthAsync(text, household, baby, zone, now, cancellationToken);

        return text.ToString();
    }

    /// <summary>Every feed, sleep, diaper and pump of the last two days, with its time: what "a few logs back" means to a parent.</summary>
    private async Task AppendRecentEntriesAsync(
        StringBuilder text, HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string At(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).ToString("ddd HH:mm", En);
        string Clock(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).ToString("HH:mm", En);
        var entries = (await store!.ListAsync(household, now - DetailWindow - TimeSpan.FromDays(1), now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry)
            .Where(e => !e.IsDeleted && GetToday.IsAbout(e, baby))
            .Where(e => e.Details is FeedingDetails or SleepDetails or DiaperDetails or PumpDetails or Coo.Domain.CareLog.Kinds.Bath.BathDetails
                or Coo.Domain.CareLog.Kinds.Development.TummyDetails)
            // A session counts while any of it is in the window; a point, a breast feed, by when it happened.
            .Where(e => (e.Timing is SessionTiming s ? s.Range.End ?? now : e.Timing.OccurredAt) >= now - DetailWindow)
            .OrderBy(e => e.Timing.OccurredAt)
            .ToList();
        if (entries.Count == 0)
            return;
        text.AppendLine(CultureInfo.InvariantCulture, $"Every entry of the last {DetailWindow.TotalHours:0} hours, oldest first:");
        foreach (var e in entries.TakeLast(MaxDetailLines))
        {
            var when = e.Timing switch
            {
                SessionTiming { Range.End: { } end } s => $"{At(s.Range.Start)}–{Clock(end)}",
                SessionTiming s => $"{At(s.Range.Start)}–now",
                _ when e.Details is BreastFeed f => $"{At(f.FirstStart)}",
                _ => At(e.Timing.OccurredAt),
            };
            // Never the note: the privacy policy promises the Advisor gets the log without notes.
            text.AppendLine(CultureInfo.InvariantCulture, $"- {when} {Describe(e, now)}");
        }
    }

    /// <summary>Medicines, temperatures, vaccines and visits of the last two weeks; every growth measurement, with its WHO percentile.</summary>
    private async Task AppendHealthAndGrowthAsync(
        StringBuilder text, HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string At(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).ToString("ddd d MMM HH:mm", En);
        var recent = (await store!.ListAsync(household, now - HealthWindow, now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry)
            .Where(e => e.Subject is BabySubject { BabyId: var id } && id == baby)
            .Where(e => e.Details is MedicineDetails or TemperatureDetails or VaccineDetails or VisitDetails)
            .OrderBy(e => e.Timing.OccurredAt)
            .ToList();
        if (recent.Count > 0)
        {
            text.AppendLine("Health, last 14 days:");
            foreach (var e in recent)
                // Never the note: the privacy policy promises the Advisor gets the log without notes.
                text.AppendLine(CultureInfo.InvariantCulture, $"- {At(e.Timing.OccurredAt)}: {Describe(e, now)}");
        }

        await AppendFoodsAndMilestonesAsync(text, household, baby, zone, now, cancellationToken);

        var profile = households is null ? null
            : (await households.ListBabiesAsync(household, cancellationToken)).FirstOrDefault(b => b.Id == baby);
        if (profile?.BirthDate is not { } birth)
            return;
        var growth = await new GetGrowth(store).ExecuteAsync(household, baby, birth, now, cancellationToken);
        if (growth.Count == 0)
            return;
        var sex = chart is null ? null : await chart.GetAsync(cancellationToken);
        text.AppendLine(sex is null ? "Growth measurements:" : $"Growth measurements, with WHO percentiles ({sex.Value.ToString().ToLowerInvariant()}s' standard):");
        foreach (var (entry, details) in growth)
        {
            var age = WhoGrowth.AgeMonths(birth, entry.Timing.OccurredAt);
            string Part(GrowthMeasure measure, string label, string unit)
            {
                if (WhoGrowth.ValueOf(details, measure) is not { } value)
                    return "";
                var percentile = sex is { } chosen ? WhoGrowth.Percentile(measure, chosen, age, value) : null;
                return string.Create(En, $"{label} {value:0.##} {unit}{(percentile is { } p ? $" (P{Math.Round(p):0})" : "")}; ");
            }

            var line = string.Create(En,
                $"- {TimeZoneInfo.ConvertTime(entry.Timing.OccurredAt, zone).ToString("d MMM yyyy", En)} (age {age:0.0} months): {Part(GrowthMeasure.Weight, "weight", "kg")}{Part(GrowthMeasure.Length, "length", "cm")}{Part(GrowthMeasure.Head, "head", "cm")}");
            text.AppendLine(line.TrimEnd(' ', ';'));
        }
    }

    /// <summary>
    /// Every solid food tried, once each with the day it was first given and any reaction ever seen with it, and every
    /// milestone with its date: what a question about allergies, weaning or development needs, however long ago.
    /// </summary>
    private async Task AppendFoodsAndMilestonesAsync(
        StringBuilder text, HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string Day(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).ToString("d MMM yyyy", En);
        var all = (await store!.ListAsync(household, now - TimeSpan.FromDays(3 * 366), now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry)
            .Where(e => !e.IsDeleted && e.Subject is BabySubject { BabyId: var id } && id == baby)
            .OrderBy(e => e.Timing.OccurredAt)
            .ToList();

        var foods = all.Where(e => e.Details is SolidsDetails)
            .GroupBy(e => ((SolidsDetails)e.Details).Food.ToLowerInvariant())
            .Select(g =>
            {
                var first = g.First();
                var reactions = g.Select(e => ((SolidsDetails)e.Details).Reaction).Where(r => r != SolidsReaction.None).Distinct().ToList();
                var how = reactions.Count == 0 ? "" : $", {string.Join(", ", reactions.Select(r => r == SolidsReaction.Reaction ? "a reaction (rash, vomiting or swelling) seen" : r.ToString().ToLowerInvariant()))}";
                return $"{((SolidsDetails)first.Details).Food} (first {Day(first.Timing.OccurredAt)}, {g.Count()}x{how})";
            })
            .ToList();
        if (foods.Count > 0)
            text.AppendLine(CultureInfo.InvariantCulture, $"Solid foods tried: {string.Join("; ", foods)}.");

        var milestones = all.Where(e => e.Details is MilestoneDetails).ToList();
        if (milestones.Count == 0)
            return;
        text.AppendLine("Milestones:");
        foreach (var e in milestones)
        {
            var m = (MilestoneDetails)e.Details;
            text.AppendLine(CultureInfo.InvariantCulture, $"- {Day(e.Timing.OccurredAt)}: {m.Title ?? m.Code.Replace('-', ' ')}");
        }
    }

    private static string Line(DayCare day, bool isToday)
    {
        var name = day.Date.ToString("ddd d MMM", En) + (isToday ? " (today so far)" : "");
        if (!day.HasEntries)
            return $"- {name}: nothing logged";
        var breast = day.BreastFeeds > 0 ? $", breast {day.BreastFeeds} (avg {Duration(day.BreastTime / day.BreastFeeds)})" : "";
        var bottle = day.BottleMl > 0 ? $", bottle {day.BottleMl:0} ml" : "";
        var pump = day.PumpedMl > 0 ? $"; pumped {day.PumpedMl:0} ml" : "";
        return string.Create(
            En,
            $"- {name}: sleep {Duration(day.Sleep)} in {day.Sleeps} sleeps (longest {Duration(day.LongestSleep)}); feeds {day.Feeds}{breast}{bottle}; diapers {day.Diapers} ({day.Wet} wet, {day.Dirty} dirty){pump}");
    }

    private static string Week(string name, TrendBucket week) => week.HasData
        ? string.Create(
            En,
            $"{name} ({week.DaysCounted} days logged), daily averages: sleep {Duration(week.Sleep)}, longest sleep {Duration(week.LongestSleep)}, feeds {week.Feeds:0.#}, bottle {week.BottleMl:0} ml, diapers {week.Diapers:0.#}, pumped {week.PumpedMl:0} ml.")
        : $"{name}: nothing logged.";

    private static string Describe(CareEntry entry, DateTimeOffset now) => entry.Details switch
    {
        BreastFeed f => $"breastfeed, left {Duration(f.LeftDuration)}, right {Duration(f.RightDuration)}",
        BottleFeed b => string.Create(En, $"bottle {b.Total.Millilitres:0} ml ({string.Join(" + ", b.Components.Select(c => c.Milk.ToString().ToLowerInvariant()))})"),
        SleepDetails when entry.Timing is SessionTiming s => $"sleep of {Duration((s.Range.End ?? now) - s.Range.Start)}",
        DiaperDetails d => $"diaper, {d.Contents.ToString().ToLowerInvariant()}",
        PumpDetails p => p.Yield is { } yield ? string.Create(En, $"pump, {yield.Total.Millilitres:0} ml") : "pump",
        Coo.Domain.CareLog.Kinds.MilkStash.MilkBagDetails b => string.Create(
            En, $"milk stash bag, {b.Volume.Millilitres:0} ml, {(b.UsedOn is not null ? "used" : b.DiscardedOn is not null ? "thrown away" : b.Place.ToString().ToLowerInvariant())}"),
        MedicineDetails m => m.Dose is { } dose ? $"medicine {m.Name}, {dose}" : $"medicine {m.Name}",
        TemperatureDetails t => string.Create(En, $"temperature {t.Celsius:0.0} °C{(t.IsFever ? " (fever)" : "")}"),
        VaccineDetails v => $"vaccine {v.Name}",
        VisitDetails v => v.With is { } with ? $"doctor visit ({with})" : "doctor visit",
        Coo.Domain.CareLog.Kinds.Bath.BathDetails => "bath",
        TummyDetails when entry.Timing is SessionTiming s => $"tummy time of {Duration((s.Range.End ?? now) - s.Range.Start)}",
        Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails p =>
            $"sleep plan{(p.IsRunning ? "" : " (stopped)")}: goals {string.Join(", ", p.Goals)}, approach {p.Approach}",
        _ => entry.KindCode.Value,
    };

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 60 ? $"{(int)span.TotalHours} h {span.Minutes:00} m" : $"{Math.Max(0, (int)span.TotalMinutes)} m";
}
