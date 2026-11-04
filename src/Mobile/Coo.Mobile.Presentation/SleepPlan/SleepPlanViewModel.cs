using CommunityToolkit.Mvvm.ComponentModel;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.SleepPlan;

/// <summary>One line of the planned day: "Nap 1", "09:45", "after 2 h 30 m awake · about 1 h 20 m".</summary>
public sealed record PlanRow(string Label, string Time, string Detail, string KindCode);

/// <summary>One measure of a check-in: "Night wakings", "2.1", "1.3".</summary>
public sealed record MeasureRow(string Label, string Before, string Now);

/// <summary>A plan that ran: "12 Oct – 2 Nov", "Fading · Fewer night wakings".</summary>
public sealed record PastPlanRow(string Dates, string Summary);

/// <summary>
/// The sleep plan page (Spec 019 §2): the intake with its safety questions and a preview, then the running plan with
/// its planned day, the approach's stored texts, the check-in when due, and the past plans. Owners and parents start,
/// answer and stop; caregivers and viewers follow (§11.3).
/// </summary>
public sealed partial class SleepPlanViewModel(
    IHouseholdStore households, GetSleepPlan get, PreviewSleepPlan preview, StartSleepPlan start, AnswerSleepCheckIn answer,
    StopSleepPlan stop, ILogAccess access, IClockFormat clock, TimeProvider time, WriteSleepPlanWords? words = null,
    IBillingService? billing = null) : ObservableObject
{
    /// <summary>The three yes/no questions of §6: feeding, weight, breathing.</summary>
    public const int Questions = 3;

    private readonly HashSet<SleepPlanGoal> _goals = [];
    private readonly bool?[] _concerns = new bool?[Questions];
    private HouseholdId? _household;
    private BabyProfile? _baby;
    private Formats _formats = new(true, VolumeUnit.Millilitres);

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private bool _isReady;

    /// <summary>False before the birth is recorded.</summary>
    [ObservableProperty]
    private bool _isBorn;

    [ObservableProperty]
    private bool _canChange;

    [ObservableProperty]
    private SleepPlanToday? _current;

    [ObservableProperty]
    private SettlingApproach _approach = SettlingApproach.Fading;

    [ObservableProperty]
    private TimeSpan? _wakeUp;

    /// <summary>The planned day the answers give, before starting.</summary>
    [ObservableProperty]
    private SleepPlanDay? _previewDay;

    /// <summary>Why there is no plan for these answers (Spec 019 §3, §6), in words.</summary>
    [ObservableProperty]
    private string _refusal = "";

    [ObservableProperty]
    private IReadOnlyList<PastPlanRow> _history = [];

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The running plan in the Advisor's words (Premium, Spec 019 §4); empty without them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWriteWords))]
    private string _planWords = "";

    /// <summary>The due check-in in the Advisor's words; empty without them.</summary>
    [ObservableProperty]
    private string _checkInWords = "";

    /// <summary>Premium: the plan can be written in the Advisor's words.</summary>
    public bool IsPremium => billing?.IsPremiumCached ?? false;

    public bool IsUnderFourMonths { get; private set; }

    public IReadOnlySet<SleepPlanGoal> Goals => _goals;

    public bool? Concern(int question) => _concerns[question];

    /// <summary>A goal and every question answered: the preview can be asked for.</summary>
    public bool CanPreview => _goals.Count > 0 && _concerns.All(c => c is not null);

    public async Task LoadAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null : (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        (_household, _baby) = (household?.Id, baby);
        _formats = new Formats(clock.Is24Hour, household?.VolumeUnit ?? VolumeUnit.Millilitres);
        BabyName = baby?.Name ?? "";
        IsBorn = baby?.IsBorn == true;
        if (household is null || baby is null || !baby.IsBorn)
        {
            (Current, History, CanChange, IsReady) = (null, [], false, true);
            return;
        }

        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        IsUnderFourMonths = BabyAge.On(baby, SleepPlans.Today(now, zone)).CorrectedDays < SleepPlanner.TrainingFromDays;
        if (IsUnderFourMonths)
            Approach = SettlingApproach.Rhythm;
        CanChange = await access.CanPlanAsync(household.Id, CancellationToken.None);
        Current = await get.ExecuteAsync(household.Id, baby, zone, now, CancellationToken.None);
        History = [.. (await get.HistoryAsync(household.Id, baby.Id, zone, CancellationToken.None)).Select(p => new PastPlanRow(
            $"{Date(p.StartedOn)} – {Date(p.StoppedOn)}",
            string.Join(" · ", [ApproachName(p.Details.Approach), .. p.Details.Goals.Select(GoalName)])))];
        (PlanWords, CheckInWords) = ("", "");
        IsReady = true;
        if (Current is { } plan)
            _ = LoadWordsAsync(household.Id, baby, plan, BabyAge.On(baby, SleepPlans.Today(now, zone)).CorrectedDays / 7, zone, send: HasConsent?.Invoke() == true);
    }

    /// <summary>
    /// The Advisor's words for the plan and a due check-in: kept ones at once; new ones are written only with Premium and
    /// once the Advisor's consent was given (<paramref name="send"/>), since the plan's facts leave the phone. Never in the
    /// way: without them the page shows the stored texts.
    /// </summary>
    private async Task LoadWordsAsync(HouseholdId household, BabyProfile baby, SleepPlanToday plan, int weeks, TimeZoneInfo zone, bool send)
    {
        if (words is null)
            return;
        try
        {
            var language = Strings.Language;
            var planFacts = SleepPlanFacts.Plan(plan, weeks);
            if (await Write(SleepPlanWordsKind.Plan, planFacts) is { } planText && Current?.Entry.Id == plan.Entry.Id)
                PlanWords = WithName(planText);
            if (plan.CheckIn is not { } checkIn)
                return;
            if (await Write(SleepPlanWordsKind.CheckIn, SleepPlanFacts.CheckIn(plan, checkIn, weeks)) is { } checkInText
                && Current?.Entry.Id == plan.Entry.Id && Current.CheckIn is not null)
                CheckInWords = WithName(checkInText);

            async Task<string?> Write(SleepPlanWordsKind kind, string facts)
            {
                var key = SleepPlanFacts.Key(plan, kind, language, facts);
                return send
                    ? await words.ExecuteAsync(household, baby.Id, kind, key, facts, language, zone.Id, CancellationToken.None)
                    : await words.KeptAsync(key, CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // The words are extra: a failure leaves the stored texts, and must not end the page.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Asks for the Advisor's consent once, then writes the plan's words (the page's "Write it").</summary>
    public async Task WriteWordsAsync()
    {
        if (!CanWriteWords || _household is not { } household || _baby is not { } baby || Current is not { } plan)
            return;
        if (EnsureConsentAsync is { } consent && !await consent())
            return;
        IsWriting = true;
        try
        {
            var now = time.GetUtcNow();
            await LoadWordsAsync(household, baby, plan, BabyAge.On(baby, SleepPlans.Today(now, time.LocalTimeZone)).CorrectedDays / 7, time.LocalTimeZone, send: true);
        }
        finally
        {
            IsWriting = false;
        }
    }

    /// <summary>The page's consent dialog (the Advisor's own); null sends without asking (tests).</summary>
    public Func<Task<bool>>? EnsureConsentAsync { get; set; }

    /// <summary>Whether the Advisor's consent was given on this phone: then new words are written without a tap.</summary>
    public Func<bool>? HasConsent { get; set; }

    /// <summary>Premium, a plan without its words yet: the page offers "Write it".</summary>
    public bool CanWriteWords => IsPremium && words is not null && Current is not null && PlanWords.Length == 0 && !IsWriting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWriteWords))]
    private bool _isWriting;

    private string WithName(string text) => text.Replace(SleepPlanFacts.Baby, BabyName, StringComparison.Ordinal);

    public void ToggleGoal(SleepPlanGoal goal)
    {
        if (!_goals.Remove(goal))
            _goals.Add(goal);
        ClearPreview();
        OnPropertyChanged(nameof(Goals));
        OnPropertyChanged(nameof(CanPreview));
    }

    public void ChooseApproach(SettlingApproach approach)
    {
        Approach = approach;
        ClearPreview();
    }

    public void ChooseWakeUp(TimeSpan? wakeUp)
    {
        WakeUp = wakeUp;
        ClearPreview();
    }

    public void Answer(int question, bool yes)
    {
        _concerns[question] = yes;
        ClearPreview();
        OnPropertyChanged(nameof(CanPreview));
    }

    public async Task PreviewAsync()
    {
        if (_household is not { } household || _baby is null || !CanPreview)
            return;
        var result = await preview.ExecuteAsync(household, _baby, Intake(), Concerned, time.LocalTimeZone, time.GetUtcNow(), CancellationToken.None);
        (PreviewDay, Refusal) = result.IsSuccess ? (result.Value, "") : (null, Strings.Error(result.Error!.Code));
    }

    public async Task<bool> StartAsync()
    {
        if (_household is not { } household || _baby is null || !CanChange || IsBusy)
            return false;
        IsBusy = true;
        try
        {
            var result = await start.ExecuteAsync(household, _baby, Intake(), Concerned, time.LocalTimeZone, time.GetUtcNow(), CancellationToken.None);
            if (result.IsFailure)
            {
                Refusal = Strings.Error(result.Error!.Code);
                return false;
            }

            ClearPreview();
            await LoadAsync();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The check-in's suggestion tried, or (<paramref name="accept"/> false) the plan kept as it is.</summary>
    public async Task AnswerCheckInAsync(bool accept)
    {
        if (Current is not { CheckIn: { } checkIn } plan || !CanChange)
            return;
        var choice = accept ? checkIn.Suggestion : new PlanAdjustment(PlanAdjustmentKind.KeepGoing);
        await answer.ExecuteAsync(plan.Entry.Id, choice, SleepPlans.Today(time.GetUtcNow(), time.LocalTimeZone), CancellationToken.None);
        await LoadAsync();
    }

    public async Task StopAsync()
    {
        if (Current is not { } plan || !CanChange)
            return;
        await stop.ExecuteAsync(plan.Entry.Id, SleepPlans.Today(time.GetUtcNow(), time.LocalTimeZone), CancellationToken.None);
        await LoadAsync();
    }

    /// <summary>The planned day as rows: wake-up, each nap with its window and length, bedtime.</summary>
    public IReadOnlyList<PlanRow> Rows(SleepPlanDay day) => Rows(day, _formats);

    public static IReadOnlyList<PlanRow> Rows(SleepPlanDay day, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        var rows = new List<PlanRow> { new(Strings.Get("Plan.WakeUpAt"), formats.Time(day.WakeUp), "", "sleep") };
        rows.AddRange(day.Naps.Select((nap, i) => new PlanRow(
            Strings.Format("Plan.Nap", i + 1), formats.Time(nap.Start),
            Strings.Format("Plan.NapDetail", Formats.Duration(nap.WindowBefore), Formats.Duration(nap.Length)), "sleep")));
        rows.Add(new PlanRow(Strings.Get("Plan.Bedtime"), formats.Time(day.Bedtime), Strings.Format("Plan.BedtimeDetail", Formats.Duration(day.LastWindow)), "sleep"));
        return rows;
    }

    /// <summary>Why the planned times are what they are.</summary>
    public IReadOnlyList<string> Notes(SleepPlanDay day) => Notes(day, BabyName);

    public static IReadOnlyList<string> Notes(SleepPlanDay day, string name)
    {
        ArgumentNullException.ThrowIfNull(day);
        return [.. day.Notes.Select(n => Strings.Format("Plan.Note." + n, name))];
    }

    public IReadOnlyList<MeasureRow> Measures(SleepCheckIn checkIn) => Measures(checkIn, _formats);

    public static IReadOnlyList<MeasureRow> Measures(SleepCheckIn checkIn, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(checkIn);
        ArgumentNullException.ThrowIfNull(formats);
        var (b, n) = (checkIn.Before, checkIn.Now);
        var rows = new List<MeasureRow>
        {
            new(Strings.Get("Plan.M.NightWakings"), Number(b.NightWakings), Number(n.NightWakings)),
            new(Strings.Get("Plan.M.Longest"), Span(b.LongestStretch), Span(n.LongestStretch)),
            new(Strings.Get("Plan.M.Night"), Span(b.NightSleep), Span(n.NightSleep)),
            new(Strings.Get("Plan.M.Day"), Span(b.DaySleep), Span(n.DaySleep)),
            new(Strings.Get("Plan.M.NapLength"), Span(b.NapLength), Span(n.NapLength)),
            new(Strings.Get("Plan.M.Bedtime"), b.Bedtime is { } bb ? formats.Time(bb) : "–", n.Bedtime is { } nb ? formats.Time(nb) : "–"),
        };
        return [.. rows.Where(r => r.Before != "–" || r.Now != "–")];
    }

    /// <summary>The suggestion in words: "Move bedtime 15 m earlier."</summary>
    public string Suggestion(PlanAdjustment adjustment) => Suggestion(adjustment, BabyName);

    public static string Suggestion(PlanAdjustment adjustment, string name)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        var minutes = Formats.Duration(TimeSpan.FromMinutes(adjustment.Minutes));
        return adjustment.Kind switch
        {
            PlanAdjustmentKind.EarlierBedtime => Strings.Format("Plan.Adj.EarlierBedtime", minutes),
            PlanAdjustmentKind.LongerWindows => Strings.Format("Plan.Adj.LongerWindows", name, minutes),
            PlanAdjustmentKind.ShorterLastNap => Strings.Format("Plan.Adj.ShorterLastNap", name, minutes),
            PlanAdjustmentKind.NewAgeSchedule => Strings.Format("Plan.Adj.NewAgeSchedule", name),
            PlanAdjustmentKind.DropANap => Strings.Format("Plan.Adj.DropANap", name),
            _ => Strings.Get("Plan.Adj.KeepGoing"),
        };
    }

    /// <summary>The stored text of an approach (Spec 019 §5), with the baby's name.</summary>
    public string SettlingText(SettlingApproach approach) => Strings.Format("Plan.Text." + approach, BabyName);

    public static string SettlingText(SettlingApproach approach, string name) => Strings.Format("Plan.Text." + approach, name);

    public static string ExpectText(SettlingApproach approach) => Strings.Get(approach switch
    {
        SettlingApproach.Rhythm => "Plan.Expect.Rhythm",
        SettlingApproach.NoCrying => "Plan.Expect.NoCrying",
        _ => "Plan.Expect.Training",
    });

    public string RoutineText => Strings.Format("Plan.RoutineText", BabyName);

    public static string Routine(string name) => Strings.Format("Plan.RoutineText", name);

    public static string Date(DateOnly day) => day.ToString("d MMM", Strings.Culture);

    public string Time(TimeOnly t) => _formats.Time(t);

    public static string GoalName(SleepPlanGoal goal) => Strings.Get("Plan.Goal." + goal);

    public static string ApproachName(SettlingApproach approach) => Strings.Get("Plan.Approach." + approach);

    /// <summary>
    /// Today's plan line (Spec 019 §2.3): "Sleep plan · day 3", then the next planned sleep after <paramref name="clock"/>, or
    /// that a check-in is ready.
    /// </summary>
    public static string? TodayLine(SleepPlanToday? plan, TimeOnly clock, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (plan is null)
            return null;
        var line = Strings.Format("Plan.TodayLine", plan.DayNumber);
        if (plan.CheckIn is not null)
            return $"{line} · {Strings.Get("Plan.TodayCheckIn")}";
        if (plan.Day is not { } day)
            return line;
        // During the planned day only: the next nap still to come, else bedtime.
        if (clock < day.WakeUp || clock >= day.Bedtime)
            return line;
        var nap = day.Naps.Select((n, i) => (Index: i + 1, n.Start)).FirstOrDefault(n => n.Start > clock);
        var (label, at) = nap.Index > 0 ? (Strings.Format("Plan.Nap", nap.Index), nap.Start) : (Strings.Get("Plan.Bedtime"), day.Bedtime);
        return $"{line} · {Strings.Format("Plan.TodayNext", label, formats.Time(at))}";
    }

    /// <summary>Today's plan line for the running plan now, or null without one.</summary>
    public string? CurrentLine() =>
        TodayLine(Current, TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime), _formats);

    private bool Concerned => _concerns.Any(c => c == true);

    private SleepPlanIntake Intake() =>
        new(_goals.ToHashSet(), IsUnderFourMonths ? SettlingApproach.Rhythm : Approach, WakeUp is { } w ? TimeOnly.FromTimeSpan(w) : null);

    private void ClearPreview() => (PreviewDay, Refusal) = (null, "");

    private static string Number(double? value) => value is { } v ? v.ToString("0.#", Strings.Culture) : "–";

    private static string Span(TimeSpan? value) => value is { } v ? Formats.Duration(v) : "–";
}
