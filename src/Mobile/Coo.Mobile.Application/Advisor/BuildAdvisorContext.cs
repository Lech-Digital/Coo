using System.Globalization;
using System.Text;
using Coo.Domain.CareLog;
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
/// of each kind, each of the last seven days, and this week against the week before. Facts only, in the family's time
/// zone, so the answer can quote them instead of guessing.
/// </summary>
public sealed class BuildAdvisorContext(
    GetToday today, GetCareTrends trends, ICareLogStore? store = null, IHouseholdStore? households = null, IGrowthChartPreference? chart = null)
{
    /// <summary>How far back medicines, temperatures, vaccines and visits are told.</summary>
    public static readonly TimeSpan HealthWindow = TimeSpan.FromDays(14);

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public async Task<string> ExecuteAsync(
        HouseholdId household, BabyId baby, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var summary = await today.ExecuteAsync(household, baby, zone, now, cancellationToken);
        var date = DayMath.DateOf(now, zone);
        var days = await trends.DaysAsync(household, baby, date.AddDays(-6), date, zone, now, cancellationToken);
        var weeks = await trends.ExecuteAsync(household, baby, TrendGrain.Week, date, 2, DayOfWeek.Monday, zone, now, cancellationToken);
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

        text.AppendLine("Per day (sleep is cut at midnight; today is not over):");
        foreach (var day in days.Values.OrderBy(d => d.Date))
            text.AppendLine(Line(day, day.Date == date));

        if (weeks is [var before, var current])
        {
            text.AppendLine(Week("This week so far", current));
            text.AppendLine(Week("Last week", before));
        }

        if (store is not null)
            await AppendHealthAndGrowthAsync(text, household, baby, zone, now, cancellationToken);

        return text.ToString();
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
        MedicineDetails m => m.Dose is { } dose ? $"medicine {m.Name}, {dose}" : $"medicine {m.Name}",
        TemperatureDetails t => string.Create(En, $"temperature {t.Celsius:0.0} °C{(t.IsFever ? " (fever)" : "")}"),
        VaccineDetails v => $"vaccine {v.Name}",
        VisitDetails v => v.With is { } with ? $"doctor visit ({with})" : "doctor visit",
        _ => entry.KindCode.Value,
    };

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 60 ? $"{(int)span.TotalHours} h {span.Minutes:00} m" : $"{Math.Max(0, (int)span.TotalMinutes)} m";
}
