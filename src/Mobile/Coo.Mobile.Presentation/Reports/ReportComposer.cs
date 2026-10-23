using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Growth;
using Coo.Mobile.Application.Reports;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Growth;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Reports;

/// <summary>
/// The report for the doctor in the app's language and the household's units: who and when, daily averages, day by day,
/// growth with WHO percentiles, and health events with their notes. What a paediatrician asks at a check-up, on one or
/// two pages.
/// </summary>
public static class ReportComposer
{
    public static ReportDocument Compose(CareReport report, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(formats);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(report.Household.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(report.GeneratedAt, zone).DateTime);
        string Date(DateOnly d) => d.ToString("d MMM yyyy", Strings.Culture);
        string Day(DateOnly d) => d.ToString("ddd d MMM", Strings.Culture);
        string When(DateTimeOffset at) => $"{Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime))} {formats.Time(at, zone)}";
        string Ml(decimal ml) => ml <= 0 ? "–" : Volume.FromMillilitres(ml) is { IsSuccess: true } v ? formats.Volume(v.Value) : "–";

        var birth = report.Baby.BirthDate!.Value;
        var subtitle = new List<string>
        {
            Strings.Format("Report.Born", Date(birth), Formats.Age(report.Baby, today)),
            Strings.Format("Report.Period", Date(report.From), Date(report.To)),
        };

        var sections = new List<ReportSection>();

        // Averages over the days that are over and have entries, as Stats counts them.
        var counted = report.Days.Where(d => d.HasEntries && d.Date < report.To).ToList();
        if (counted.Count > 0)
        {
            double Avg(Func<DayCare, double> pick) => counted.Average(pick);
            var sleep = TimeSpan.FromTicks((long)Avg(d => d.Sleep.Ticks));
            var longest = TimeSpan.FromTicks((long)Avg(d => d.LongestSleep.Ticks));
            var lines = new List<string>
            {
                Strings.Format("Report.AvgSleep", Formats.Duration(sleep), Avg(d => d.Sleeps).ToString("0.#", Strings.Culture), Formats.Duration(longest)),
                Strings.Format("Report.AvgFeeds", Avg(d => d.Feeds).ToString("0.#", Strings.Culture)),
                Strings.Format("Report.AvgDiapers", Avg(d => d.Diapers).ToString("0.#", Strings.Culture), Avg(d => d.Wet).ToString("0.#", Strings.Culture), Avg(d => d.Dirty).ToString("0.#", Strings.Culture)),
            };
            var bottle = (decimal)Avg(d => (double)d.BottleMl);
            if (bottle > 0)
                lines.Add(Strings.Format("Report.AvgBottle", Ml(Math.Round(bottle))));
            var pumped = (decimal)Avg(d => (double)d.PumpedMl);
            if (pumped > 0)
                lines.Add(Strings.Format("Report.AvgPumped", Ml(Math.Round(pumped))));
            lines.Add(Strings.Plural("Report.DaysCounted", counted.Count));
            sections.Add(new ReportSection(Strings.Get("Report.Averages"), lines));
        }

        sections.Add(new ReportSection(
            Strings.Get("Report.Daily"),
            report.Days.Count == 0 ? [Strings.Get("Report.NothingLogged")] : [],
            report.Days.Count == 0 ? null : new ReportTable(
                [
                    (Strings.Get("Report.Col.Date"), 1.3f), (Strings.Get("Report.Col.Sleep"), 1f), (Strings.Get("Report.Col.Sleeps"), 0.7f),
                    (Strings.Get("Report.Col.Feeds"), 0.7f), (Strings.Get("Report.Col.Breast"), 1f), (Strings.Get("Report.Col.Bottle"), 0.9f),
                    (Strings.Get("Report.Col.Diapers"), 1f), (Strings.Get("Report.Col.Pumped"), 0.9f),
                ],
                [.. report.Days.Select(d => (IReadOnlyList<string>)(d.HasEntries
                    ?
                    [
                        Day(d.Date) + (d.Date == report.To ? " *" : ""),
                        Formats.Duration(d.Sleep),
                        d.Sleeps.ToString(Strings.Culture),
                        d.Feeds.ToString(Strings.Culture),
                        d.BreastFeeds > 0 ? $"{d.BreastFeeds} · {Formats.Duration(d.BreastTime)}" : "–",
                        Ml(d.BottleMl),
                        $"{d.Diapers} ({d.Wet}/{d.Dirty})",
                        Ml(d.PumpedMl),
                    ]
                    : [Day(d.Date), "–", "–", "–", "–", "–", "–", "–"]))])));

        // Growth since birth, not only in the period: a doctor reads the curve, not a week of it.
        var growthRows = report.Growth.Select(g =>
        {
            var age = WhoGrowth.AgeMonths(birth, g.Entry.Timing.OccurredAt);
            string Cell(int? stored, Func<int, string> show, GrowthMeasure measure)
            {
                if (stored is not { } v)
                    return "–";
                var percentile = report.Sex is { } sex && WhoGrowth.ValueOf(g.Details, measure) is { } value
                    ? WhoGrowth.Percentile(measure, sex, age, value)
                    : null;
                return percentile is { } p ? $"{show(v)} (P{Math.Clamp(Math.Round(p), 1, 99):0})" : show(v);
            }

            return (IReadOnlyList<string>)
            [
                Date(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(g.Entry.Timing.OccurredAt, zone).DateTime)),
                Strings.Format("Report.AgeMonths", age.ToString("0.0", Strings.Culture)),
                Cell(g.Details.WeightGrams, formats.Weight, GrowthMeasure.Weight),
                Cell(g.Details.LengthMm, formats.Length, GrowthMeasure.Length),
                Cell(g.Details.HeadMm, formats.Length, GrowthMeasure.Head),
            ];
        }).ToList();
        sections.Add(new ReportSection(
            Strings.Get("Report.Growth"),
            growthRows.Count == 0 ? [Strings.Get("Report.NoGrowth")]
                : report.Sex is { } chosen ? [Strings.Get(chosen == ChartSex.Girl ? "Report.WhoGirls" : "Report.WhoBoys")] : [],
            growthRows.Count == 0 ? null : new ReportTable(
                [(Strings.Get("Report.Col.Date"), 1.2f), (Strings.Get("Report.Col.Age"), 0.9f), (Strings.Get("Edit.Weight"), 1.3f), (Strings.Get("Edit.Length"), 1.2f), (Strings.Get("Growth.Head"), 1.2f)],
                growthRows)));

        sections.Add(new ReportSection(
            Strings.Get("Report.Health"),
            report.Health.Count == 0 ? [Strings.Get("Report.NoHealth")] : [],
            report.Health.Count == 0 ? null : new ReportTable(
                [(Strings.Get("Report.Col.Time"), 1.2f), (Strings.Get("Report.Col.What"), 1.8f), (Strings.Get("Report.Col.Note"), 2f)],
                HealthRows(report, formats, When, d => d.ToString("d MMM", Strings.Culture)))));

        return new ReportDocument(
            Strings.Format("Report.Title", report.Baby.Name),
            subtitle,
            sections,
            Strings.Format("Report.Footer", Date(today)));
    }

    /// <summary>A medicine given this many times without a note is one line, not one per dose.</summary>
    public const int RepeatedDoses = 3;

    /// <summary>
    /// Each health event on its own line, except a medicine given again and again without notes (a daily vitamin D),
    /// which becomes one line: "10–23 Oct (14×) · Vitamin D · 400 IU". Lines in order of their first time.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> HealthRows(CareReport report, Formats formats, Func<DateTimeOffset, string> when, Func<DateOnly, string> day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(report.Household.TimeZoneId);
        DateOnly DateOf(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var repeated = report.Health
            .Where(e => e.Details is MedicineDetails && e.Note is null)
            .GroupBy(e => formats.Title(e, report.GeneratedAt))
            .Where(g => g.Count() >= RepeatedDoses)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<(DateTimeOffset First, IReadOnlyList<string> Cells)>();
        foreach (var (title, doses) in repeated)
        {
            var (first, last) = (DateOf(doses[0].Timing.OccurredAt), DateOf(doses[^1].Timing.OccurredAt));
            rows.Add((doses[0].Timing.OccurredAt, [$"{day(first)} – {day(last)} ({doses.Count}×)", title, ""]));
        }

        foreach (var e in report.Health)
        {
            if (e.Details is MedicineDetails && e.Note is null && repeated.ContainsKey(formats.Title(e, report.GeneratedAt)))
                continue;
            rows.Add((e.Timing.OccurredAt, [when(e.Timing.OccurredAt), formats.Title(e, report.GeneratedAt), e.Note?.Text ?? ""]));
        }

        return [.. rows.OrderBy(r => r.First).Select(r => r.Cells)];
    }

    /// <summary>"coo-lena-2026-10-23.pdf": ASCII only, so every mail app and drive keeps the name.</summary>
    public static string FileName(CareReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var name = new string([.. report.Baby.Name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => char.IsAsciiLetterOrDigit(c))
            .Select(char.ToLowerInvariant)]);
        return $"coo-{(name.Length == 0 ? "baby" : name)}-{report.To:yyyy-MM-dd}.pdf";
    }
}
