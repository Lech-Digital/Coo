using Coo.Domain.Common;

namespace Coo.Domain.Insights;

/// <summary>What the parent wants a sleep plan to change (Spec 019 §2.1); several may be chosen.</summary>
public enum SleepPlanGoal
{
    FallAsleepEasier,
    FewerNightWakings,
    LongerNaps,
    EarlierBedtime,
    LaterMorning,
    PredictableDay,
    DropANap,
}

/// <summary>
/// How the parent settles the baby (Spec 019 §5). Under 4 months corrected only <see cref="Rhythm"/>: no sleep training.
/// Extinction ("cry it out") is not offered (owner, 2026-11-03).
/// </summary>
public enum SettlingApproach
{
    /// <summary>Wake windows, routine and day/night cues only.</summary>
    Rhythm,

    /// <summary>Parent present, gradually less help.</summary>
    Fading,

    /// <summary>Parent sits by the cot and moves the chair further every few nights.</summary>
    Chair,

    /// <summary>Short checks at intervals the parent chooses.</summary>
    CheckAndConsole,

    /// <summary>Routine, timing and fading only, with no crying left alone; slower, and the plan says so.</summary>
    NoCrying,
}

/// <param name="WakeUp">When the day should start; null takes the baby's usual morning, or 07:00.</param>
public sealed record SleepPlanIntake(IReadOnlySet<SleepPlanGoal> Goals, SettlingApproach Approach, TimeOnly? WakeUp = null);

/// <summary>Spec 019 §6: what the parent answered, and what the log shows, about the baby's health now.</summary>
/// <param name="UnwellLately">A temperature of 38 °C or more, or a medicine other than a vitamin, in the last 3 days.</param>
/// <param name="ParentConcern">A yes to any of the feeding, weight or breathing questions.</param>
public sealed record SleepPlanSafety(bool UnwellLately, bool ParentConcern);

/// <summary>One nap of the planned day: the wake window before it, when it starts, and how long it is likely.</summary>
public sealed record PlannedNap(TimeSpan WindowBefore, TimeOnly Start, TimeSpan Length);

/// <summary>Why a planned time is what it is, for the plan's "why" line and its texts.</summary>
public enum PlanNote
{
    /// <summary>Under 4 months: rhythm and routine only; the approach chosen applies from then.</summary>
    RhythmOnlyUnderFourMonths,

    /// <summary>Her own wake windows were used where there were enough of them.</summary>
    OwnWindows,

    /// <summary>Her own nap lengths were used.</summary>
    OwnNaps,

    /// <summary>The bedtime came out later than usual for her age; an earlier start or a short late nap helps.</summary>
    BedtimeLate,

    /// <summary>The bedtime came out earlier than usual for her age.</summary>
    BedtimeEarly,

    /// <summary>One nap fewer than typical, as asked (she is at an age where that nap goes).</summary>
    NapDropped,
}

/// <summary>A planned day (Spec 019 §4): wake-up, naps, bedtime (the end of the last window), and the notes behind them.</summary>
public sealed record SleepPlanDay(
    TimeOnly WakeUp,
    IReadOnlyList<PlannedNap> Naps,
    TimeSpan LastWindow,
    TimeOnly Bedtime,
    IReadOnlyList<PlanNote> Notes,
    SleepNormBand Norms,
    WakeWindowBand Windows,
    string Version);

/// <summary>What the log shows over some days (Spec 019 §2.4), averaged per day or night; null where nothing was logged.</summary>
public sealed record SleepMeasures(
    int Days,
    double? NightWakings,
    TimeSpan? LongestStretch,
    TimeSpan? NightSleep,
    TimeSpan? DaySleep,
    double? Naps,
    TimeSpan? NapLength,
    TimeOnly? Bedtime,
    TimeOnly? WakeUp);

/// <summary>The one change a check-in suggests (Spec 019 §4), bounded; <see cref="KeepGoing"/> when nothing should change.</summary>
public enum PlanAdjustmentKind
{
    KeepGoing,

    /// <summary>She has moved into the next age band: a new day for her age.</summary>
    NewAgeSchedule,

    /// <summary>Bedtime earlier by <see cref="PlanAdjustment.Minutes"/>.</summary>
    EarlierBedtime,

    /// <summary>Every wake window longer by <see cref="PlanAdjustment.Minutes"/>, within her age's range.</summary>
    LongerWindows,

    /// <summary>The last nap of the day kept to <see cref="PlanAdjustment.Minutes"/>.</summary>
    ShorterLastNap,

    /// <summary>She is dropping a nap already: plan one nap fewer.</summary>
    DropANap,
}

public sealed record PlanAdjustment(PlanAdjustmentKind Kind, int Minutes = 0);

/// <summary>A check-in (Spec 019 §2.4): the week before the plan, the days since, and the suggestion.</summary>
public sealed record SleepCheckIn(SleepMeasures Before, SleepMeasures Now, PlanAdjustment Suggestion);

public static class SleepPlanErrors
{
    /// <summary>Under 2 weeks: no plan; feeding on demand and sleep as it comes.</summary>
    public const string TooYoung = "plan.too-young";

    /// <summary>Unwell lately: pause, and see how she is first.</summary>
    public const string Unwell = "plan.unwell";

    /// <summary>A feeding, weight or breathing worry: the doctor or health visitor first.</summary>
    public const string SeeDoctor = "plan.see-doctor";
}

/// <summary>
/// The sleep planner (Spec 019 §4): a pure function of the baby's corrected age, her logged sleeps, the parent's
/// answers and <c>now</c>. It computes the plan's numbers and the check-in's measures and suggestion; the words come
/// from stored texts, or from the Advisor with Premium. Never medical advice; the safety checks of §6 come first.
/// </summary>
public static class SleepPlanner
{
    public const string Version = "plan-v1";

    /// <summary>No plan before 2 weeks old.</summary>
    public const int YoungestDays = 14;

    /// <summary>Sleep training (anything but <see cref="SettlingApproach.Rhythm"/>) from 4 months corrected (Spec 019 §3).</summary>
    public const int TrainingFromDays = 120;

    /// <summary>Her own medians count from this many observations.</summary>
    public const int OwnFrom = 3;

    /// <summary>The look-back for her own patterns, as the predictor's.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    private static readonly TimeOnly DefaultWakeUp = new(7, 0);

    /// <summary>The approach the plan uses: <see cref="SettlingApproach.Rhythm"/> under 4 months whatever was chosen.</summary>
    public static SettlingApproach ApproachFor(SettlingApproach chosen, int correctedDays) =>
        correctedDays < TrainingFromDays ? SettlingApproach.Rhythm : chosen;

    /// <summary>The planned day for now; a failure for the cases of Spec 019 §3 and §6, with nothing planned.</summary>
    public static Result<SleepPlanDay> Plan(
        SleepPlanIntake intake, SleepPlanSafety safety, IEnumerable<SleepSpan> sleeps, int correctedDays, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        if (correctedDays < YoungestDays)
            return Result<SleepPlanDay>.Failure(SleepPlanErrors.TooYoung);
        if (safety.ParentConcern)
            return Result<SleepPlanDay>.Failure(SleepPlanErrors.SeeDoctor);
        if (safety.UnwellLately)
            return Result<SleepPlanDay>.Failure(SleepPlanErrors.Unwell);

        var norms = SleepNorms.For(correctedDays);
        var windows = WakeWindowPrior.For(correctedDays);
        var own = Observe(sleeps, now, zone);
        var notes = new List<PlanNote>();
        if (correctedDays < TrainingFromDays && intake.Approach != SettlingApproach.Rhythm)
            notes.Add(PlanNote.RhythmOnlyUnderFourMonths);

        var naps = norms.Naps;
        if (intake.Goals.Contains(SleepPlanGoal.DropANap) && naps > norms.FewestNaps)
        {
            naps--;
            notes.Add(PlanNote.NapDropped);
        }

        var wakeUp = intake.WakeUp ?? own.WakeUp ?? DefaultWakeUp;
        var planned = new List<PlannedNap>(naps);
        var at = wakeUp;
        var usedOwnWindows = false;
        var usedOwnNaps = false;
        for (var i = 1; i <= naps; i++)
        {
            var window = Window(windows, own, i, naps, ref usedOwnWindows);
            var length = NapLength(norms, own, i, naps, ref usedOwnNaps);
            var start = at.Add(window);
            planned.Add(new PlannedNap(window, start, length));
            at = start.Add(length);
        }

        var last = Window(windows, own, naps + 1, naps, ref usedOwnWindows);
        var bedtime = at.Add(last);
        if (usedOwnWindows)
            notes.Add(PlanNote.OwnWindows);
        if (usedOwnNaps)
            notes.Add(PlanNote.OwnNaps);
        if (norms.HasBedtime)
        {
            if (Later(bedtime, norms.BedtimeLatest!.Value))
                notes.Add(PlanNote.BedtimeLate);
            else if (Later(norms.BedtimeEarliest!.Value, bedtime))
                notes.Add(PlanNote.BedtimeEarly);
        }

        return new SleepPlanDay(wakeUp, planned, last, bedtime, notes, norms, windows, Version);
    }

    /// <summary>
    /// A check-in: the week before <paramref name="planStarted"/> against the days since (at most the last 7), and the
    /// one bounded change worth trying (Spec 019 §4).
    /// </summary>
    public static SleepCheckIn CheckIn(
        SleepPlanDay plan, SleepPlanIntake intake, IEnumerable<SleepSpan> sleeps, int correctedDays, DateTimeOffset planStarted,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(sleeps);
        var all = sleeps.ToList();
        var before = Measure(all, planStarted - TimeSpan.FromDays(7), planStarted, zone);
        var since = planStarted > now - TimeSpan.FromDays(7) ? planStarted : now - TimeSpan.FromDays(7);
        var current = Measure(all, since, now, zone);
        return new SleepCheckIn(before, current, Suggest(plan, intake, before, current, correctedDays));
    }

    /// <summary>The rules of Spec 019 §4, first match wins, so a check-in never suggests two changes at once.</summary>
    internal static PlanAdjustment Suggest(SleepPlanDay plan, SleepPlanIntake intake, SleepMeasures before, SleepMeasures now, int correctedDays)
    {
        var norms = SleepNorms.For(correctedDays);
        if (norms.FromDays != plan.Norms.FromDays)
            return new(PlanAdjustmentKind.NewAgeSchedule);
        if (now.Days < 3)
            return new(PlanAdjustmentKind.KeepGoing);

        // Already dropping a nap on most days, and old enough for fewer.
        if (now.Naps is { } naps && naps <= plan.Naps.Count - 0.6 && plan.Naps.Count > norms.FewestNaps)
            return new(PlanAdjustmentKind.DropANap);

        // Short naps although the windows are not yet at the long end of her age: a little more time awake.
        if (now.NapLength is { } nap && nap < TimeSpan.FromMinutes(40) && plan.Naps.Count > 0
            && plan.Naps.Max(n => n.WindowBefore) + TimeSpan.FromMinutes(15) <= plan.Windows.High)
            return new(PlanAdjustmentKind.LongerWindows, 15);

        // Night wakings no better, and more day sleep than her age needs: keep the last nap short.
        if (now.NightWakings is { } wakings && wakings >= (before.NightWakings ?? 0) && wakings >= 1
            && now.DaySleep is { } day && now.NightSleep is { } night && day + night > norms.TotalHigh && plan.Naps.Count > 1)
            return new(PlanAdjustmentKind.ShorterLastNap, 30);

        // Asked for an earlier bedtime, and she is still going down later than planned.
        if (intake.Goals.Contains(SleepPlanGoal.EarlierBedtime) && now.Bedtime is { } bed
            && Later(bed, plan.Bedtime.Add(TimeSpan.FromMinutes(10))))
            return new(PlanAdjustmentKind.EarlierBedtime, 15);

        return new(PlanAdjustmentKind.KeepGoing);
    }

    /// <summary>
    /// The planned day with the changes the parent accepted at check-ins, oldest first, each kept within her age's range:
    /// longer windows up to the band's high end, an earlier bedtime by a shorter last window down to the low end, the
    /// last nap kept short. <see cref="PlanAdjustmentKind.DropANap"/> and <see cref="PlanAdjustmentKind.NewAgeSchedule"/>
    /// change what is planned, not the day, so the caller plans again for them; here they and
    /// <see cref="PlanAdjustmentKind.KeepGoing"/> change nothing.
    /// </summary>
    public static SleepPlanDay Adjust(SleepPlanDay day, IEnumerable<PlanAdjustment> accepted)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(accepted);
        var windows = day.Naps.Select(n => n.WindowBefore).Append(day.LastWindow).ToList();
        var lengths = day.Naps.Select(n => n.Length).ToList();
        var changed = false;
        foreach (var adjustment in accepted)
        {
            var by = TimeSpan.FromMinutes(adjustment.Minutes);
            switch (adjustment.Kind)
            {
                case PlanAdjustmentKind.LongerWindows:
                    for (var i = 0; i < windows.Count; i++)
                        windows[i] = windows[i] + by > day.Windows.High ? Max(windows[i], day.Windows.High) : windows[i] + by;
                    changed = true;
                    break;
                case PlanAdjustmentKind.EarlierBedtime:
                    windows[^1] = windows[^1] - by < day.Windows.Low ? Min(windows[^1], day.Windows.Low) : windows[^1] - by;
                    changed = true;
                    break;
                case PlanAdjustmentKind.ShorterLastNap when lengths.Count > 0:
                    lengths[^1] = Min(lengths[^1], Clamp(by, TimeSpan.FromMinutes(20), TimeSpan.FromHours(3)));
                    changed = true;
                    break;
            }
        }

        if (!changed)
            return day;

        var naps = new List<PlannedNap>(lengths.Count);
        var at = day.WakeUp;
        for (var i = 0; i < lengths.Count; i++)
        {
            var start = at.Add(windows[i]);
            naps.Add(new PlannedNap(windows[i], start, lengths[i]));
            at = start.Add(lengths[i]);
        }

        return day with { Naps = naps, LastWindow = windows[^1], Bedtime = at.Add(windows[^1]) };
    }

    /// <summary>
    /// What the log shows from <paramref name="from"/> to <paramref name="to"/>: per night (a night counted on the day it
    /// began), per day, and the usual bedtime and wake-up. Days with no sleep logged are left out.
    /// </summary>
    public static SleepMeasures Measure(IReadOnlyList<SleepSpan> sleeps, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sleeps);
        ArgumentNullException.ThrowIfNull(zone);
        var done = sleeps.Where(s => s.End is { } end && end <= to && s.Start >= from - TimeSpan.FromDays(1)).OrderBy(s => s.Start).ToList();
        if (done.Count == 0)
            return new SleepMeasures(0, null, null, null, null, null, null, null, null);

        var window = SleepClassifier.LearnNightWindow(done, zone);
        var classes = SleepClassifier.Classify(done, window, zone);
        var nights = new List<(DateOnly Day, int Segments, TimeSpan Sleep, TimeSpan Longest, TimeOnly Start, TimeOnly End)>();
        var days = new Dictionary<DateOnly, (int Naps, TimeSpan Sleep)>();
        var napLengths = new List<TimeSpan>();
        for (var i = 0; i < done.Count; i++)
        {
            var s = done[i];
            if (s.Start < from)
                continue;
            var local = TimeZoneInfo.ConvertTime(s.Start, zone).DateTime;
            if (classes[i] == SleepClass.Nap)
            {
                var day = DateOnly.FromDateTime(local);
                days[day] = days.TryGetValue(day, out var d) ? (d.Naps + 1, d.Sleep + s.Length!.Value) : (1, s.Length!.Value);
                napLengths.Add(s.Length!.Value);
                continue;
            }

            // A night segment continues the night before it when it follows within a night waking.
            var continues = i > 0 && classes[i - 1] == SleepClass.Night && s.Start - done[i - 1].End!.Value <= SleepClassifier.NightWakingAtMost;
            if (continues && nights.Count > 0)
            {
                var n = nights[^1];
                nights[^1] = (n.Day, n.Segments + 1, n.Sleep + s.Length!.Value, s.Length!.Value > n.Longest ? s.Length!.Value : n.Longest, n.Start,
                    TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.End!.Value, zone).DateTime));
            }
            else
            {
                // A night that starts after midnight belongs to the evening before.
                var day = DateOnly.FromDateTime(local.Hour < 12 ? local.AddDays(-1) : local);
                nights.Add((day, 1, s.Length!.Value, s.Length!.Value, TimeOnly.FromDateTime(local),
                    TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.End!.Value, zone).DateTime)));
            }
        }

        var counted = nights.Select(n => n.Day).Concat(days.Keys).Distinct().Count();
        return new SleepMeasures(
            counted,
            nights.Count > 0 ? nights.Average(n => n.Segments - 1) : null,
            nights.Count > 0 ? Average(nights.Select(n => n.Longest)) : null,
            nights.Count > 0 ? Average(nights.Select(n => n.Sleep)) : null,
            days.Count > 0 ? Average(days.Values.Select(d => d.Sleep)) : null,
            days.Count > 0 ? days.Values.Average(d => d.Naps) : null,
            napLengths.Count > 0 ? Median(napLengths) : null,
            nights.Count > 0 ? MedianEvening(nights.Select(n => n.Start)) : null,
            nights.Count > 0 ? MedianMorning(nights.Select(n => n.End)) : null);
    }

    /// <summary>Her own patterns over the look-back: wake windows per position, nap lengths per nap, the usual morning.</summary>
    internal sealed record OwnPatterns(
        IReadOnlyDictionary<int, TimeSpan> Windows, IReadOnlyDictionary<int, TimeSpan> Naps, TimeOnly? WakeUp);

    internal static OwnPatterns Observe(IEnumerable<SleepSpan> sleeps, DateTimeOffset now, TimeZoneInfo zone)
    {
        var recent = sleeps.Where(s => s.End is { } end && end <= now && s.Start >= now - LookBack).OrderBy(s => s.Start).ToList();
        if (recent.Count == 0)
            return new OwnPatterns(new Dictionary<int, TimeSpan>(), new Dictionary<int, TimeSpan>(), null);

        var window = SleepClassifier.LearnNightWindow(recent, zone);
        var classes = SleepClassifier.Classify(recent, window, zone);
        var (observations, _) = SleepPredictor.Walk(recent, classes, PredictorParameters.Default);
        var windows = observations
            .GroupBy(o => o.Position)
            .Where(g => g.Count() >= OwnFrom)
            .ToDictionary(g => g.Key, g => Median([.. g.Select(o => o.Length)]));

        var naps = new Dictionary<int, List<TimeSpan>>();
        var mornings = new List<TimeOnly>();
        int? index = null;
        for (var i = 0; i < recent.Count; i++)
        {
            if (classes[i] == SleepClass.Night)
            {
                index = 0;
                continue;
            }

            if (index is null)
                continue;
            if (index == 0 && i > 0 && classes[i - 1] == SleepClass.Night)
                mornings.Add(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(recent[i - 1].End!.Value, zone).DateTime));
            index++;
            if (!naps.TryGetValue(index.Value, out var list))
                naps[index.Value] = list = [];
            list.Add(recent[i].Length!.Value);
        }

        return new OwnPatterns(
            windows,
            naps.Where(n => n.Value.Count >= OwnFrom).ToDictionary(n => n.Key, n => Median(n.Value)),
            mornings.Count >= OwnFrom ? MedianMorning(mornings) : null);
    }

    /// <summary>Window <paramref name="position"/> (1 is the morning's): hers where known, kept within her age's range.</summary>
    private static TimeSpan Window(WakeWindowBand band, OwnPatterns own, int position, int naps, ref bool usedOwn)
    {
        // The prior is laid out for the band's typical nap count; with a nap dropped the day stretches over fewer windows.
        var scaled = (int)Math.Round((position - 1) * (band.TypicalNaps / (double)Math.Max(1, naps))) + 1;
        if (own.Windows.TryGetValue(position, out var mine))
        {
            usedOwn = true;
            return RoundToFive(Clamp(mine, band.Low, band.High));
        }

        return RoundToFive(band.At(Math.Min(scaled, band.TypicalNaps + 1)));
    }

    /// <summary>Nap <paramref name="index"/>: hers where known, else typical; the last nap of three or more is short.</summary>
    private static TimeSpan NapLength(SleepNormBand norms, OwnPatterns own, int index, int naps, ref bool usedOwn)
    {
        if (own.Naps.TryGetValue(index, out var mine))
        {
            usedOwn = true;
            return RoundToFive(Clamp(mine, TimeSpan.FromMinutes(20), TimeSpan.FromHours(3)));
        }

        return naps >= 3 && index == naps ? TimeSpan.FromMinutes(Math.Min(45, norms.NapLength.TotalMinutes)) : norms.NapLength;
    }

    /// <summary>Whether <paramref name="a"/> is later in the evening than <paramref name="b"/>, after midnight counting as later.</summary>
    internal static bool Later(TimeOnly a, TimeOnly b) => Evening(a) > Evening(b);

    private static double Evening(TimeOnly t) => t.Hour < 12 ? t.ToTimeSpan().TotalMinutes + (24 * 60) : t.ToTimeSpan().TotalMinutes;

    private static TimeOnly MedianEvening(IEnumerable<TimeOnly> times)
    {
        var minutes = times.Select(Evening).OrderBy(m => m).ToList();
        var median = minutes[minutes.Count / 2] % (24 * 60);
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(median));
    }

    private static TimeOnly MedianMorning(IEnumerable<TimeOnly> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        return sorted[sorted.Count / 2];
    }

    private static TimeSpan Median(List<TimeSpan> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static TimeSpan Average(IEnumerable<TimeSpan> values)
    {
        var list = values.ToList();
        return TimeSpan.FromTicks((long)list.Average(v => v.Ticks));
    }

    private static TimeSpan RoundToFive(TimeSpan value) => TimeSpan.FromMinutes(Math.Round(value.TotalMinutes / 5) * 5);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
