using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;

namespace Coo.Codecs.Tests;

/// <summary>
/// The golden fixtures and the domain value each must decode to. Expectations are built with the Domain
/// factories here, never read from JSON, so a codec bug cannot hide in both (Spec 003 §9).
/// </summary>
internal static class Fixtures
{
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly TimeSpan Pdt = TimeSpan.FromHours(-7);

    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IReadOnlyDictionary<string, EntryDetails> Expected { get; } = new Dictionary<string, EntryDetails>
    {
        ["feeding/v1/breast-completed-two-sides.json"] = BreastFeed.Create(
        [
            new(BreastSide.Left, At(14, 0), At(14, 10)),
            new(BreastSide.Right, At(14, 10), At(14, 15)),
        ], isCompleted: true).Value,
        ["feeding/v1/breast-running.json"] = BreastFeed.Create(
        [
            new(BreastSide.Left, At(3, 5), At(3, 12)),
            new(BreastSide.Right, At(3, 12), null),
        ], isCompleted: false).Value,
        ["feeding/v1/breast-paused.json"] = BreastFeed.Create([new(BreastSide.Right, At(3, 5), At(3, 9))], isCompleted: false).Value,
        ["feeding/v1/bottle-formula.json"] = BottleFeed.Of(Milk.Formula, Ml(120)).Value,
        ["feeding/v1/bottle-mixed.json"] = BottleFeed.Create(
        [
            new(Milk.ExpressedBreastMilk, Ml(60)),
            new(Milk.Formula, Ml(90.5m)),
        ]).Value,
        ["feeding/v1/breast-non-utc-offset.json"] = BreastFeed.Create(
            [new(BreastSide.Left, new(2026, 9, 30, 6, 0, 0, Pdt), new(2026, 9, 30, 6, 20, 0, Pdt))], isCompleted: true).Value,
        ["diaper/v1/wet.json"] = new DiaperDetails(DiaperContents.Wet),
        ["diaper/v1/dirty.json"] = new DiaperDetails(DiaperContents.Dirty),
        ["diaper/v1/mixed.json"] = new DiaperDetails(DiaperContents.Mixed),
        ["sleep/v1/no-override.json"] = new SleepDetails(),
        ["sleep/v1/nap.json"] = new SleepDetails(SleepKindOverride.Nap),
        ["sleep/v1/night.json"] = new SleepDetails(SleepKindOverride.Night),
        ["pump/v1/no-yield.json"] = new PumpDetails(),
        ["pump/v1/left-only.json"] = new PumpDetails(PumpYield.BySide(Ml(70), null).Value),
        ["pump/v1/both-sides.json"] = new PumpDetails(PumpYield.BySide(Ml(70), Ml(55.5m)).Value),
        ["pump/v1/total.json"] = new PumpDetails(PumpYield.OfTotal(Ml(125))),
        ["growth/v1/all.json"] = GrowthDetails.Create(5_120, 584, 392).Value,
        ["growth/v1/weight-only.json"] = GrowthDetails.Create(4_380, null, null).Value,
        ["medicine/v1/with-dose.json"] = MedicineDetails.Create("Vitamin D", "400 IU").Value,
        ["medicine/v1/no-dose.json"] = MedicineDetails.Create("Paracetamol", null).Value,
        ["temperature/v1/fever.json"] = TemperatureDetails.Create(38.4m).Value,
        ["vaccine/v1/name.json"] = VaccineDetails.Create("MMR").Value,
        ["bath/v1/bath.json"] = new Coo.Domain.CareLog.Kinds.Bath.BathDetails(),
        ["tummy/v1/tummy.json"] = new Coo.Domain.CareLog.Kinds.Development.TummyDetails(),
        ["solids/v1/reaction.json"] = Coo.Domain.CareLog.Kinds.Development.SolidsDetails.Create("Peanut butter", "1 spoon", Coo.Domain.CareLog.Kinds.Development.SolidsReaction.Reaction).Value,
        ["solids/v1/food-only.json"] = Coo.Domain.CareLog.Kinds.Development.SolidsDetails.Create("Carrot puree", null, Coo.Domain.CareLog.Kinds.Development.SolidsReaction.None).Value,
        ["milestone/v1/preset.json"] = Coo.Domain.CareLog.Kinds.Development.MilestoneDetails.Create("first-smile", null).Value,
        ["milestone/v1/custom.json"] = Coo.Domain.CareLog.Kinds.Development.MilestoneDetails.Create("custom", "Clapped hands").Value,
        ["sleepplan/v1/running.json"] = Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails.Create(
            [Coo.Domain.Insights.SleepPlanGoal.EarlierBedtime, Coo.Domain.Insights.SleepPlanGoal.FewerNightWakings],
            Coo.Domain.Insights.SettlingApproach.Fading, new TimeOnly(7, 0)).Value,
        ["sleepplan/v1/stopped.json"] = Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails.Create(
            [Coo.Domain.Insights.SleepPlanGoal.LongerNaps], Coo.Domain.Insights.SettlingApproach.Rhythm, null,
            [new Coo.Domain.CareLog.Kinds.SleepPlan.AcceptedAdjustment(new DateOnly(2026, 11, 10), Coo.Domain.Insights.PlanAdjustmentKind.LongerWindows, 15)],
            new DateOnly(2026, 12, 1)).Value,
        ["milkbag/v1/frozen.json"] = Coo.Domain.CareLog.Kinds.MilkStash.MilkBagDetails.Create(
            Volume.FromMillilitres(120).Value, Coo.Domain.CareLog.Kinds.MilkStash.MilkPlace.Freezer, label: "night milk").Value,
        ["milkbag/v1/thawed-used.json"] = Coo.Domain.CareLog.Kinds.MilkStash.MilkBagDetails.Create(
            Volume.FromMillilitres(90).Value, Coo.Domain.CareLog.Kinds.MilkStash.MilkPlace.Fridge,
            DateTimeOffset.FromUnixTimeMilliseconds(1793091600000), new DateOnly(2026, 11, 5)).Value,
        ["visit/v1/with.json"] = VisitDetails.Create("Paediatrician").Value,
        ["visit/v1/empty.json"] = VisitDetails.Create(null).Value,
    };

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Directory, relativePath)).Trim();

    public static IEnumerable<string> OnDisk() =>
        System.IO.Directory.GetFiles(Directory, "*.json", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Directory, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    public static Volume Ml(decimal ml) => Volume.FromMillilitres(ml).Value;

    private static DateTimeOffset At(int hour, int minute) => new(2026, 9, 30, hour, minute, 0, Cest);
}
