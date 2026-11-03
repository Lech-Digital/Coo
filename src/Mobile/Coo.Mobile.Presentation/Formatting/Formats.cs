using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Formatting;

/// <summary>How numbers, times and entries read in the UI (Spec 006 §8). Pure: the clock format and units are passed in.</summary>
public sealed class Formats(bool is24Hour, VolumeUnit unit)
{
    public VolumeUnit Unit => unit;

    /// <summary>"14:05" or "2:05 PM", following the phone's setting, not the language (ADR 0006 §5).</summary>
    public string Time(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString(is24Hour ? "HH:mm" : "h:mm tt", Strings.Culture);

    /// <summary>"14:05" or "2:05 PM" in the given zone: the chart uses the household's, so it reads the same on every phone.</summary>
    public string Time(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(is24Hour ? "HH:mm" : "h:mm tt", Strings.Culture);

    /// <summary>A wall-clock time of a plan, "19:15" or "7:15 PM".</summary>
    public string Time(TimeOnly time) => time.ToString(is24Hour ? "HH:mm" : "h:mm tt", Strings.Culture);

    /// <summary>"1 h 20 m", or "45 m".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = Math.Max(0, (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes >= 60
            ? Strings.Format("Duration.HoursMinutes", minutes / 60, minutes % 60)
            : Strings.Format("Duration.Minutes", minutes);
    }

    /// <summary>Timer digits: "08:42" under an hour, "1:08:42" from an hour.</summary>
    public static string Clock(TimeSpan span)
    {
        var s = span < TimeSpan.Zero ? TimeSpan.Zero : span;
        return s.TotalHours >= 1
            ? s.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : s.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"120 ml", or "4 oz" in an ounces household.</summary>
    public string Volume(Volume volume) => unit == VolumeUnit.Ounces
        ? $"{Math.Round(volume.Ounces, 1).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Oz")}"
        : $"{Math.Round(volume.Millilitres).ToString("0", Strings.Culture)} {Strings.Get("Unit.Ml")}";

    public static string Side(BreastSide side, bool shortForm) =>
        Strings.Get(shortForm ? (side == BreastSide.Left ? "Side.L" : "Side.R") : (side == BreastSide.Left ? "Side.Left" : "Side.Right"));

    public static string Contents(DiaperContents contents) => Strings.Get(contents switch
    {
        DiaperContents.Wet => "Diaper.Wet",
        DiaperContents.Dirty => "Diaper.Dirty",
        _ => "Diaper.Mixed",
    });

    public static string MilkName(IReadOnlyList<BottleComponent> components) => components.Count > 1
        ? Strings.Get("Milk.Both")
        : Strings.Get(components[0].Milk == Milk.Formula ? "Milk.Formula" : "Milk.Breast");

    /// <summary>
    /// A timeline title: "Breast · left 10 m · right 5 m", "Breast · left 12 m · right 8 m · in 3 parts",
    /// "Bottle · Formula · 120 ml", "Diaper · Wet". A breast feed's length is its feeding time, never its span.
    /// </summary>
    public string Title(StoredEntry stored, DateTimeOffset now) => stored switch
    {
        LiveEntry { Entry: var e } => Title(e, now),
        ReadOnlyEntry => Strings.Get("Entry.ReadOnly"),
        _ => Strings.Get("Entry.Quarantined"),
    };

    /// <summary>The person's label, or the classifier's for this one sleep ("Decide for me").</summary>
    private static bool IsNight(SleepDetails sleep, SessionTiming timing) =>
        Coo.Domain.Insights.SleepClassifier.ClassifyOne(
            new Coo.Domain.Insights.SleepSpan(timing.Range.Start, timing.Range.End, sleep.KindOverride),
            TimeOnly.FromDateTime(timing.Range.Start.ToLocalTime().DateTime)) == Coo.Domain.Insights.SleepClass.Night;

    public string Title(CareEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Details switch
        {
            BreastFeed f when entry.Timing.IsInProgress => Strings.Format("Entry.Breast", Strings.Get("Entry.Running")),
            BreastFeed { Parts: > 1 } f => Strings.Format("Entry.Breast", $"{BreastSummary(f)} · {Parts(f)}"),
            BreastFeed f => Strings.Format("Entry.Breast", BreastSummary(f)),
            BottleFeed b => Strings.Format("Entry.Bottle", MilkName(b.Components), Volume(b.Total)),
            DiaperDetails d => Strings.Format("Entry.Diaper", Contents(d.Contents)),
            SleepDetails when entry.Timing.IsInProgress => Strings.Get("Entry.Sleeping"),
            SleepDetails s => Strings.Format(
                IsNight(s, (SessionTiming)entry.Timing) ? "Entry.Sleep" : "Entry.Nap",
                Duration(((SessionTiming)entry.Timing).Range.Duration(now))),
            PumpDetails { Yield: { } y } => Strings.Format("Entry.Pump", Volume(y.Total)),
            PumpDetails => Strings.Get("Entry.PumpNoAmount"),
            GrowthDetails g => Strings.Format("Entry.Growth", Growth(g)),
            MedicineDetails { Dose: { } dose } m => $"{m.Name} · {dose}",
            MedicineDetails m => m.Name,
            TemperatureDetails t => Strings.Format(t.IsFever ? "Entry.Fever" : "Entry.Temperature", Temperature(t.Celsius)),
            VaccineDetails v => Strings.Format("Entry.Vaccine", v.Name),
            VisitDetails { With: { } with } => Strings.Format("Entry.VisitWith", with),
            VisitDetails => Strings.Get("Kind.Visit"),
            Coo.Domain.CareLog.Kinds.Bath.BathDetails => Strings.Get("Kind.Bath"),
            Coo.Domain.CareLog.Kinds.Development.TummyDetails when entry.Timing.IsInProgress => Strings.Get("Entry.TummyRunning"),
            Coo.Domain.CareLog.Kinds.Development.TummyDetails => Strings.Format("Entry.Tummy", Duration(((SessionTiming)entry.Timing).Range.Duration(now))),
            Coo.Domain.CareLog.Kinds.Development.SolidsDetails f => Solids(f),
            Coo.Domain.CareLog.Kinds.Development.MilestoneDetails m => Milestone(m),
            Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails { IsRunning: false } => Strings.Get("Entry.SleepPlanStopped"),
            Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails => Strings.Get("Kind.SleepPlan"),
            _ => entry.KindCode.Value,
        };
    }

    private bool Imperial => unit == VolumeUnit.Ounces;

    /// <summary>"Carrot puree · 3 spoons · liked", the reaction left out when there was nothing to say.</summary>
    public static string Solids(Coo.Domain.CareLog.Kinds.Development.SolidsDetails solids)
    {
        ArgumentNullException.ThrowIfNull(solids);
        var parts = new List<string> { solids.Food };
        if (solids.Amount is { } amount)
            parts.Add(amount);
        if (solids.Reaction != Coo.Domain.CareLog.Kinds.Development.SolidsReaction.None)
            parts.Add(Strings.Get("Solids." + solids.Reaction).ToLower(Strings.Culture));
        return string.Join(" · ", parts);
    }

    /// <summary>A milestone's name: the parent's own words, or the preset's ("First smile").</summary>
    public static string Milestone(Coo.Domain.CareLog.Kinds.Development.MilestoneDetails milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        return milestone.Title ?? Strings.Get("Milestone." + milestone.Code);
    }

    /// <summary>"4.38 kg · 58.4 cm · head 39.2 cm": what was measured, in the household's units.</summary>
    public string Growth(GrowthDetails growth)
    {
        ArgumentNullException.ThrowIfNull(growth);
        var parts = new List<string>();
        if (growth.WeightGrams is { } g)
            parts.Add(Weight(g));
        if (growth.LengthMm is { } l)
            parts.Add(Length(l));
        if (growth.HeadMm is { } h)
            parts.Add($"{Strings.Get("Edit.Head")} {Length(h)}");
        return string.Join(" · ", parts);
    }

    /// <summary>"4.38 kg" or "9.66 lb".</summary>
    public string Weight(int grams) => Imperial
        ? $"{(grams / 453.59237m).ToString("0.##", Strings.Culture)} {Strings.Get("Unit.Lb")}"
        : $"{(grams / 1000m).ToString("0.###", Strings.Culture)} {Strings.Get("Unit.Kg")}";

    /// <summary>"58.4 cm" or "23 in".</summary>
    public string Length(int millimetres) => Imperial
        ? $"{(millimetres / 25.4m).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.In")}"
        : $"{(millimetres / 10m).ToString("0.#", Strings.Culture)} {Strings.Get("Unit.Cm")}";

    /// <summary>"38.4 °C" or "101.1 °F".</summary>
    public string Temperature(decimal celsius) => Imperial
        ? $"{(celsius * 9m / 5m + 32m).ToString("0.0", Strings.Culture)} °F"
        : $"{celsius.ToString("0.0", Strings.Culture)} °C";

    /// <summary>"in 3 parts" (Spec 011d §5), for a feed in two or more parts.</summary>
    public static string Parts(BreastFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return Strings.Plural("Entry.Parts", feed.Parts);
    }

    /// <summary>"left 10 m · right 5 m": each side used, with its time.</summary>
    public static string BreastSummary(BreastFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var parts = new List<string>();
        if (feed.LeftDuration > TimeSpan.Zero)
            parts.Add(Strings.Format("Entry.BreastSide", Side(BreastSide.Left, false), Duration(feed.LeftDuration)));
        if (feed.RightDuration > TimeSpan.Zero)
            parts.Add(Strings.Format("Entry.BreastSide", Side(BreastSide.Right, false), Duration(feed.RightDuration)));
        return string.Join(" · ", parts);
    }

    /// <summary>"5 mo", "3 wk" or "4 days", from the corrected age (ADR 0008 §2), with "(corrected)" when it differs.</summary>
    public static string Age(BabyProfile baby, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(baby);
        var age = BabyAge.On(baby, today);
        var birth = baby.BirthDate!.Value;
        var reference = age.IsCorrected ? birth.AddDays(age.ActualDays - age.CorrectedDays) : birth;
        var months = BabyAge.WholeMonths(reference, today);
        var text = age.CorrectedDays < 14 ? Strings.Plural("Age.Days", age.CorrectedDays)
            : months < 2 ? Strings.Plural("Age.Weeks", age.CorrectedWeeks)
            : Strings.Plural("Age.Months", months);
        return age.IsCorrected ? Strings.Format("Age.Corrected", text) : text;
    }
}
