using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
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

    /// <summary>"1 h 20 m", or "45 m".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = Math.Max(0, (int)span.TotalMinutes);
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

    /// <summary>A timeline title: "Breast · left 10 m · right 5 m", "Bottle · Formula · 120 ml", "Diaper · Wet".</summary>
    public string Title(StoredEntry stored, DateTimeOffset now) => stored switch
    {
        LiveEntry { Entry: var e } => Title(e, now),
        ReadOnlyEntry => Strings.Get("Entry.ReadOnly"),
        _ => Strings.Get("Entry.Quarantined"),
    };

    public string Title(CareEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Details switch
        {
            BreastFeed f when entry.Timing.IsInProgress => Strings.Format("Entry.Breast", Strings.Get("Entry.Running")),
            BreastFeed f => Strings.Format("Entry.Breast", BreastSummary(f)),
            BottleFeed b => Strings.Format("Entry.Bottle", MilkName(b.Components), Volume(b.Total)),
            DiaperDetails d => Strings.Format("Entry.Diaper", Contents(d.Contents)),
            SleepDetails when entry.Timing.IsInProgress => Strings.Get("Entry.Sleeping"),
            SleepDetails s => Strings.Format(
                s.KindOverride == SleepKindOverride.Night ? "Entry.Sleep" : "Entry.Nap",
                Duration(((SessionTiming)entry.Timing).Range.Duration(now))),
            PumpDetails { Yield: { } y } => Strings.Format("Entry.Pump", Volume(y.Total)),
            PumpDetails => Strings.Get("Entry.PumpNoAmount"),
            _ => entry.KindCode.Value,
        };
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
