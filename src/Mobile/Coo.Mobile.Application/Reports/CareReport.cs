using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Growth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Reports;

/// <summary>
/// What a report for the doctor holds (roadmap increment 16): the baby, the period, each day's care, and every growth
/// measurement and health event. Facts only; wording and layout are the presentation's.
/// </summary>
public sealed record CareReport(
    BabyProfile Baby,
    HouseholdSettings Household,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DayCare> Days,
    IReadOnlyList<(CareEntry Entry, GrowthDetails Details)> Growth,
    ChartSex? Sex,
    IReadOnlyList<CareEntry> Health);

/// <summary>Gathers a <see cref="CareReport"/> for the last <c>days</c> days, today included, for the family on screen.</summary>
public sealed class BuildCareReport(
    IHouseholdStore households, GetCareTrends trends, ICareLogStore store, IGrowthChartPreference chart, TimeProvider time)
{
    /// <returns>Null without a family, a baby or a birth date.</returns>
    public async Task<CareReport?> ExecuteAsync(int days, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null : (await households.ListBabiesAsync(household.Id, cancellationToken)).FirstOrDefault(b => !b.IsDeleted);
        if (household is null || baby?.BirthDate is not { } birth)
            return null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetUtcNow();
        var to = DayMath.DateOf(now, zone);
        var from = to.AddDays(1 - days);
        if (from < birth)
            from = birth;

        var byDay = await trends.DaysAsync(household.Id, baby.Id, from, to, zone, now, cancellationToken);
        var growth = await new GetGrowth(store).ExecuteAsync(household.Id, baby.Id, birth, now, cancellationToken);
        var start = DayMath.Day(from, zone).Start;
        var health = (await store.ListAsync(household.Id, start, now.AddMinutes(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry)
            .Where(e => e.Subject is BabySubject { BabyId: var id } && id == baby.Id)
            .Where(e => e.Details is MedicineDetails or TemperatureDetails or VaccineDetails or VisitDetails)
            .OrderBy(e => e.Timing.OccurredAt)
            .ToList();
        return new CareReport(
            baby, household, from, to, now,
            [.. byDay.Values.OrderBy(d => d.Date)],
            growth,
            await chart.GetAsync(cancellationToken),
            health);
    }
}

/// <summary>A document as plain parts, laid out by the platform: a title, lines under it, sections, and a footer.</summary>
public sealed record ReportDocument(string Title, IReadOnlyList<string> Subtitle, IReadOnlyList<ReportSection> Sections, string Footer);

/// <summary>A heading, then paragraphs, then a table when there is one.</summary>
public sealed record ReportSection(string Heading, IReadOnlyList<string> Paragraphs, ReportTable? Table = null);

/// <summary>Columns with relative widths, and rows of cells, one per column.</summary>
public sealed record ReportTable(IReadOnlyList<(string Header, float Width)> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Writes a document as a PDF and opens the phone's share sheet for it (mail, WhatsApp, Drive, print).</summary>
public interface IReportExporter
{
    /// <returns>False when it could not be written or shared.</returns>
    Task<bool> ShareAsync(ReportDocument document, string fileName, CancellationToken cancellationToken);
}
