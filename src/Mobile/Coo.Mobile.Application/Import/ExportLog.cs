using System.Globalization;
using System.Text;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Import;

/// <summary>
/// The whole log as a CSV file the parent keeps or takes elsewhere: one row per entry, local times of the family's time
/// zone, plain units (minutes, millilitres, grams, millimetres, °C), oldest first. <see cref="LogFileReader"/> reads it
/// back, so the same file restores a log in Coo. Made on the phone; it goes only where the parent sends it.
/// </summary>
public sealed class ExportLog(IHouseholdStore households, ICareLogStore store)
{
    public static readonly string[] Header =
    [
        "Type", "Start", "End", "Duration", "Left minutes", "Right minutes", "Amount ml", "Milk", "Diaper",
        "Name", "Dose", "Temperature C", "Weight g", "Length mm", "Head mm", "Notes",
    ];

    /// <returns>The CSV text, or null when there is no family yet.</returns>
    public async Task<string?> ExecuteAsync(CancellationToken cancellationToken)
    {
        var (household, _) = await PrepareQuickLog.ShownAsync(households, cancellationToken);
        if (household is null)
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var entries = (await store.ListAsync(household.Id, DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry).Where(e => !e.IsDeleted).OrderBy(e => e.Timing.OccurredAt);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', Header));
        foreach (var entry in entries)
            csv.AppendLine(string.Join(',', Row(entry, zone).Select(Quote)));
        return csv.ToString();
    }

    internal static string[] Row(CareEntry entry, TimeZoneInfo zone)
    {
        string Local(DateTimeOffset? at) => at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        string Number(decimal? n) => n?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
        string Minutes(TimeSpan span) => span > TimeSpan.Zero ? ((int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) : "";
        string Duration(TimeSpan? span) => span is { } s && s > TimeSpan.Zero ? $"{(int)s.TotalHours}:{s.Minutes:00}" : "";

        var start = entry.Timing.OccurredAt;
        DateTimeOffset? end = entry.Timing is SessionTiming { Range.End: { } e } ? e : null;
        var row = new string[Header.Length];
        Array.Fill(row, "");
        row[1] = Local(start);
        row[2] = Local(end);
        row[3] = Duration(end - start);
        row[15] = entry.Note?.Text ?? "";
        switch (entry.Details)
        {
            case BreastFeed feed:
                row[0] = "Breastfeeding";
                row[1] = Local(feed.FirstStart);
                row[2] = Local(feed.Segments[^1].End);
                row[3] = Duration(feed.TotalDuration);
                row[4] = Minutes(feed.LeftDuration);
                row[5] = Minutes(feed.RightDuration);
                break;
            case BottleFeed bottle:
                row[0] = "Bottle";
                row[6] = Number(bottle.Total.Millilitres);
                row[7] = string.Join(" + ", bottle.Components.Select(c => c.Milk == Milk.Formula ? "formula" : "breast milk").Distinct());
                break;
            case DiaperDetails diaper:
                row[0] = "Diaper";
                row[8] = diaper.Contents switch { DiaperContents.Dirty => "poo", DiaperContents.Mixed => "both", _ => "pee" };
                break;
            case SleepDetails:
                row[0] = "Sleep";
                break;
            case PumpDetails pump:
                row[0] = "Pump";
                row[6] = Number(pump.Yield?.Total.Millilitres);
                break;
            case MedicineDetails medicine:
                row[0] = "Medicine";
                row[9] = medicine.Name;
                row[10] = medicine.Dose ?? "";
                break;
            case TemperatureDetails temperature:
                row[0] = "Temperature";
                row[11] = Number(temperature.Celsius);
                break;
            case GrowthDetails growth:
                row[0] = "Growth";
                row[12] = growth.WeightGrams?.ToString(CultureInfo.InvariantCulture) ?? "";
                row[13] = growth.LengthMm?.ToString(CultureInfo.InvariantCulture) ?? "";
                row[14] = growth.HeadMm?.ToString(CultureInfo.InvariantCulture) ?? "";
                break;
            case VaccineDetails vaccine:
                row[0] = "Vaccine";
                row[9] = vaccine.Name;
                break;
            case Coo.Domain.CareLog.Kinds.Bath.BathDetails:
                row[0] = "Bath";
                break;
            case Coo.Domain.CareLog.Kinds.Development.TummyDetails:
                row[0] = "Tummy time";
                break;
            case Coo.Domain.CareLog.Kinds.Development.SolidsDetails solids:
                // The food as the name, the amount as the dose, the reaction in the diaper column's place for contents.
                row[0] = "Solid food";
                row[9] = solids.Food;
                row[10] = solids.Amount ?? "";
                row[8] = solids.Reaction == Coo.Domain.CareLog.Kinds.Development.SolidsReaction.None ? "" : solids.Reaction.ToString().ToLowerInvariant();
                break;
            case Coo.Domain.CareLog.Kinds.Development.MilestoneDetails milestone:
                row[0] = "Milestone";
                row[9] = milestone.Title ?? milestone.Code.Replace('-', ' ');
                break;
            case VisitDetails visit:
                row[0] = "Doctor visit";
                row[9] = visit.With ?? "";
                break;
            default:
                row[0] = entry.Details.GetType().Name;
                break;
        }

        return row;
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
}
