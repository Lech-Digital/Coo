using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using FsCheck;
using FsCheck.Fluent;

namespace Coo.Codecs.Tests;

/// <summary>
/// One generator of valid details per kind, built through the Domain factories. A test fails if a kind has
/// none, so a new kind cannot skip the round-trip properties (Spec 003 §9).
/// </summary>
internal static class Generators
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyDictionary<string, Gen<EntryDetails>> ByKind { get; } = new Dictionary<string, Gen<EntryDetails>>
    {
        ["feeding"] = Gen.OneOf(BreastFeeds().Select(d => (EntryDetails)d), Bottles().Select(d => (EntryDetails)d)),
        ["diaper"] = Gen.Elements(DiaperContents.Wet, DiaperContents.Dirty, DiaperContents.Mixed)
            .Select(c => (EntryDetails)new DiaperDetails(c)),
        ["sleep"] = Gen.Elements<SleepKindOverride?>(null, SleepKindOverride.Nap, SleepKindOverride.Night)
            .Select(k => (EntryDetails)new SleepDetails(k)),
        ["pump"] = PumpYields().Select(y => (EntryDetails)new PumpDetails(y)),
        ["growth"] =
            (from weight in Gen.Elements<int?>(null, 300, 3_420, 7_015, 30_000)
             from length in Gen.Elements<int?>(null, 200, 512, 1_300)
             from head in Gen.Elements<int?>(null, 200, 345, 600)
             where weight is not null || length is not null || head is not null
             select (EntryDetails)GrowthDetails.Create(weight, length, head).Value),
        ["medicine"] =
            from name in Gen.Elements("Vitamin D", "Paracetamol", "Ibuprofen", "Żelazo", "Amoxicillin")
            from dose in Gen.Elements<string?>(null, "400 IU", "2.5 ml", "1 drop")
            select (EntryDetails)MedicineDetails.Create(name, dose).Value,
        ["temperature"] = Gen.Choose(340, 430).Select(t => (EntryDetails)TemperatureDetails.Create(t / 10m).Value),
        ["vaccine"] = Gen.Elements("DTaP-IPV-Hib-HepB", "MMR", "Rotavirus", "Pneumococcal").Select(n => (EntryDetails)VaccineDetails.Create(n).Value),
        ["visit"] = Gen.Elements<string?>(null, "Paediatrician", "Midwife", "Consultatiebureau").Select(w => (EntryDetails)VisitDetails.Create(w).Value),
    };

    /// <summary>Instants across a year, at offsets from UTC−12 to UTC+14, including half and quarter hours.</summary>
    public static Gen<DateTimeOffset> Instants() =>
        from minutes in Gen.Choose(0, 525_600)
        from offsetMinutes in Gen.Elements(-720, -420, -210, 0, 60, 120, 330, 345, 840)
        select new DateTimeOffset(Origin.AddMinutes(minutes).DateTime, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

    private static Gen<BreastFeed> BreastFeeds() =>
        from start in Instants()
        from sides in Gen.ListOf(Gen.Elements(BreastSide.Left, BreastSide.Right)).Where(l => l.Count is >= 1 and <= 4)
        from durations in Gen.ArrayOf(Gen.Choose(1, 60), 4)
        from gaps in Gen.ArrayOf(Gen.Choose(0, 10), 4)
        from completed in Gen.Elements(true, false)
        from lastOpen in Gen.Elements(true, false)
        select BuildBreast(start, [.. sides], durations, gaps, completed, lastOpen);

    private static BreastFeed BuildBreast(
        DateTimeOffset start, BreastSide[] sides, int[] durations, int[] gaps, bool completed, bool lastOpen)
    {
        var segments = new List<BreastSegment>();
        var cursor = start;
        for (var i = 0; i < sides.Length; i++)
        {
            var end = cursor.AddMinutes(durations[i]);
            var open = !completed && lastOpen && i == sides.Length - 1;
            segments.Add(new BreastSegment(sides[i], cursor, open ? null : end));
            cursor = end.AddMinutes(gaps[i]);
        }

        return BreastFeed.Create(segments, completed).Value;
    }

    private static Gen<BottleFeed> Bottles() =>
        from mixed in Gen.Elements(true, false)
        from first in Volumes(4000)
        from second in Volumes(4000)
        from milk in Gen.Elements(Milk.ExpressedBreastMilk, Milk.Formula)
        select mixed
            ? BottleFeed.Create([new(Milk.ExpressedBreastMilk, first), new(Milk.Formula, second)]).Value
            : BottleFeed.Of(milk, first).Value;

    private static Gen<PumpYield?> PumpYields() =>
        from shape in Gen.Choose(0, 4)
        from left in Volumes(5000)
        from right in Volumes(5000)
        select shape switch
        {
            0 => null,
            1 => PumpYield.BySide(left, null).Value,
            2 => PumpYield.BySide(null, right).Value,
            3 => PumpYield.BySide(left, right).Value,
            _ => (PumpYield?)PumpYield.OfTotal(left),
        };

    /// <summary>0.1 ml steps up to <paramref name="maxTenths"/> tenths.</summary>
    private static Gen<Volume> Volumes(int maxTenths) =>
        Gen.Choose(1, maxTenths).Select(tenths => Volume.FromMillilitres(tenths / 10m).Value);
}
