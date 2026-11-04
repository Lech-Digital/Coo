using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Today;

/// <summary>The milk choice in the bottle panel (Spec 006 §5).</summary>
public enum BottleMilk
{
    Formula,
    Breast,
    Both,
}

/// <summary>
/// The Today screen (Spec 006 §4–§6). It lays out the <see cref="IHomeSection"/>s that apply to the current stage and
/// palette, keeps them current, and owns the sheets. It knows nothing about MAUI.
/// </summary>
public sealed partial class TodayViewModel : ObservableObject
{
    private readonly IReadOnlyList<IHomeSection> _all;
    private readonly IHouseholdStore _households;
    private readonly GetToday _getToday;
    private readonly HomeActions _actions;
    private readonly ThemeController _theme;
    private readonly IClockFormat _clock;
    private readonly RecordBirth _recordBirth;
    private readonly TimeProvider _time;
    private readonly GetAttention _attention;
    private readonly WhoLabelsSource _whoSource;
    private WhoLabels _who = WhoLabels.SignedOut;
    private readonly UndoService _undo;
    private int _attentionCount;

    private HouseholdSettings? _household;
    private BabyProfile? _baby;
    private TodaySummary? _summary;
    private EntryId? _pumpForYield;
    private AwakeCheck? _awakeCheck;

    [ObservableProperty]
    private HomeSheet _sheet;

    [ObservableProperty]
    private string _toast = "";

    [ObservableProperty]
    private decimal _bottleAmount;

    [ObservableProperty]
    private decimal _secondAmount;

    [ObservableProperty]
    private BottleMilk _milk = BottleMilk.Formula;

    [ObservableProperty]
    private string _pumpLeft = "";

    [ObservableProperty]
    private string _pumpRight = "";

    [ObservableProperty]
    private DateTime _birthDate;

    [ObservableProperty]
    private string _awakeTitle = "";

    [ObservableProperty]
    private string _awakeSince = "";

    private readonly Coo.Mobile.Application.Billing.IBillingService _billing;
    private readonly IDevicePreferences _preferences;
    private readonly Coo.Mobile.Application.Households.BabyAvatarSyncService? _avatarSync;
    private string? _avatarRef;
    private readonly Coo.Mobile.Application.Insights.GetSleepPrediction? _getSleep;
    private Coo.Domain.Insights.SleepPrediction? _sleep;
    private Coo.Domain.Insights.PredictionTrackRecord? _sleepRecord;
    private readonly Coo.Mobile.Application.Insights.GetSleepPlan? _getPlan;
    private Coo.Mobile.Application.Insights.SleepPlanToday? _plan;
    private readonly Coo.Mobile.Application.CareLog.ILogAccess? _logAccess;
    private readonly Coo.Mobile.Application.Households.BabyFocus? _focus;
    private readonly Coo.Mobile.Application.CareLog.GetMilkStash? _getStash;
    private Coo.Mobile.Application.CareLog.MilkStashSummary? _stash;
    private List<BabyProfile> _siblings = [];
    private IReadOnlyList<SiblingGlance> _siblingGlances = [];
    private bool _logForAll;

    /// <summary>Set by the page: Coo's own explanation before Android asks for notifications; false is "Not now".</summary>
    public Func<Task<bool>>? PrimeNotificationsAsync { get; set; }

    /// <summary>This person follows the family without logging (a Viewer): Today shows the day, without its buttons.</summary>
    public bool IsViewOnly { get; private set; }

    public TodayViewModel(
        IEnumerable<IHomeSection> sections, IHouseholdStore households, GetToday getToday, HomeActions actions,
        ThemeController theme, IClockFormat clock, RecordBirth recordBirth, TimeProvider time, GetAttention attention, UndoService undo,
        WhoLabelsSource who, Coo.Mobile.Application.Preferences.IReviewPrompter reviewPrompter, Coo.Mobile.Application.Billing.IBillingService billing,
        IDevicePreferences preferences, Coo.Mobile.Application.Households.BabyAvatarSyncService? avatarSync = null,
        Coo.Mobile.Application.Insights.GetSleepPrediction? getSleep = null,
        Coo.Mobile.Application.Notifications.IReminderScheduler? notifications = null,
        Coo.Mobile.Application.CareLog.ILogAccess? logAccess = null,
        Coo.Mobile.Application.Insights.GetSleepPlan? getPlan = null,
        Coo.Mobile.Application.Households.BabyFocus? focus = null,
        Coo.Mobile.Application.CareLog.GetMilkStash? getStash = null)
    {
        _getStash = getStash;
        _logAccess = logAccess;
        _focus = focus;
        if (_focus is not null)
            _focus.Changed += async () => await RefreshAsync();
        _getPlan = getPlan;
        _billing = billing;
        _getSleep = getSleep;
        _preferences = preferences;
        _avatarSync = avatarSync;
        _billing.EntitlementChanged += () => Tick();
        if (_avatarSync != null)
        {
            _avatarSync.AvatarChanged += async (_, _) =>
            {
                if (_baby != null)
                {
                    _avatarRef = await _preferences.GetBabyAvatarAsync(_baby.Id.Value, CancellationToken.None);
                    Relayout();
                }
            };
        }
        (_attention, _undo, _whoSource) = (attention, undo, who);
        _all = [.. sections.OrderBy(s => s.Order)];
        (_households, _getToday, _actions, _theme, _clock, _recordBirth, _time) = (households, getToday, actions, theme, clock, recordBirth, time);
        _actions.Done += async (_, outcome) =>
        {
            Sheet = HomeSheet.None;
            Toast = outcome.Message;
            if (outcome.Succeeded)
                _ = reviewPrompter.CheckAndPromptAsync(CancellationToken.None);
            await RefreshAsync();

            // A timer just started: its clock and buttons go in the notification bar, which needs the permission (Android 13+).
            if (outcome.Succeeded && _summary?.Running.Count > 0 && notifications is not null
                && (PrimeNotificationsAsync is not { } prime || await prime()))
                _ = notifications.EnsurePermissionAsync();
        };
        _actions.SheetRequested += (_, request) => OpenSheet(request.Sheet, request.Entry);
        _actions.IsPump = id => _summary?.Running.Any(e => e.Id == id && e.Details is PumpDetails) == true;
        _actions.RunningSleep = () => _summary?.Running.FirstOrDefault(e => e.Details is Coo.Domain.CareLog.Kinds.Sleep.SleepDetails) is { } sleep
            ? (sleep.Id, sleep.Timing.OccurredAt)
            : null;
        _actions.AwakeCheckRequested += (_, check) =>
        {
            _awakeCheck = check;
            AwakeTitle = Strings.Format("Awake.Title", _baby?.Name ?? "");
            AwakeSince = Strings.Format("Hero.Since", Formats().Time(check.AsleepSince));
            Sheet = HomeSheet.AwakeCheck;
        };
        _theme.PaletteChanged += (_, _) => Relayout();
    }

    public ObservableCollection<IHomeSection> Sections { get; } = [];

    /// <summary>Today's local date from the injected clock, for date pickers' upper bound.</summary>
    public DateTime Today => _time.GetLocalNow().Date;

    public BabyProfile? Baby => _baby;

    /// <summary>The family's other babies (twins, siblings), for the chip's picker.</summary>
    public IReadOnlyList<BabyProfile> Siblings => _siblings;

    /// <summary>Shows another of the family's babies: every screen follows (<see cref="Coo.Mobile.Application.Households.BabyFocus"/>).</summary>
    public async Task FocusAsync(BabyId baby)
    {
        if (_focus is not null)
        {
            await _focus.FocusAsync(baby, CancellationToken.None);
            return;
        }

        await _households.FocusBabyAsync(baby, CancellationToken.None);
        await RefreshAsync();
    }

    /// <summary>Twins and siblings: turns "Log for both" on or off; it stays until changed or the app restarts.</summary>
    public async Task ToggleLogForAllAsync()
    {
        _logForAll = !_logForAll;
        await RefreshAsync();
    }

    /// <summary>Each born sibling's day at a glance: asleep or awake and for how long, and the last feed.</summary>
    private async Task<IReadOnlyList<SiblingGlance>> GlancesAsync()
    {
        if (_household is null || _siblings.Count == 0)
            return [];
        var now = _time.GetLocalNow();
        var glances = new List<SiblingGlance>();
        foreach (var sibling in _siblings)
        {
            var summary = sibling.IsBorn ? await _getToday.ExecuteAsync(_household.Id, sibling.Id, Zone(), now, CancellationToken.None) : null;
            glances.Add(new SiblingGlance(sibling.Id, sibling.Name, SiblingsSection.Line(summary, now), sibling.IsBorn));
        }

        return glances;
    }

    public HomeStage Stage => _baby?.IsBorn == false ? HomeStage.Expecting : HomeStage.Born;

    /// <summary>True when the household has no baby yet: the host shows setup (Spec 006 §3).</summary>
    public bool NeedsSetup { get; private set; }

    public string UnitLabel => Strings.Get(_household?.VolumeUnit == VolumeUnit.Ounces ? "Unit.Oz" : "Unit.Ml");

    /// <summary>For the page's "Earlier…" choices (Spec 009 §3.2).</summary>
    /// <summary>Starts tummy time's timer, from "Earlier…" (the running card stops it).</summary>
    public Task StartTummyAsync() => _actions.StartTummyAsync();

    /// <summary>"Running" or "12 min today": the tummy time line in "Earlier…".</summary>
    public string TummySubtitle() =>
        _summary?.Running.Any(e => e.Details is Coo.Domain.CareLog.Kinds.Development.TummyDetails) == true ? Strings.Get("Tile.Running")
        : _summary?.TummyToday is { TotalMinutes: >= 1 } done ? Strings.Format("Earlier.TummyToday", Formatting.Formats.Duration(done))
        : Strings.Get("Earlier.TummySubtitle");

    public bool IsShown(string kindCode) => _household is null || KindVisibility.IsShown(_household, kindCode);

    private decimal Step => _household?.VolumeUnit == VolumeUnit.Ounces ? 0.5m : 10m;

    /// <summary>Loads everything from the store. Called on appearing and after every action.</summary>
    public async Task RefreshAsync()
    {
        _household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (_household is null)
            return;

        var babies = (await _households.ListBabiesAsync(_household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).ToList();
        _baby = babies.Count > 0 ? babies[0] : null;
        _siblings = [.. babies.Skip(1)];
        NeedsSetup = _baby is null;
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(Baby));
        if (_baby is null)
            return;

        _avatarRef = await _preferences.GetBabyAvatarAsync(_baby.Id.Value, CancellationToken.None);

        if (_avatarSync != null)
        {
            _ = _avatarSync.SyncBabyAvatarAsync(_household.Id.Value, _baby.Id.Value, CancellationToken.None);
        }

        // Twins and siblings: "Log for both" logs a diaper, bottle or sleep for every born baby of the family.
        var others = _siblings.Where(b => b.IsBorn).ToList();
        _logForAll &= others.Count > 0 && _baby.IsBorn;
        _actions.Context = new HomeContext(_household.Id, _baby.Id, _logForAll ? [.. others.Select(b => b.Id)] : null);
        var names = babies.ToDictionary(b => b.Id, b => b.Name);
        _actions.BabyNames = id => names.GetValueOrDefault(id, "");
        _summary = _baby.IsBorn
            ? await _getToday.ExecuteAsync(_household.Id, _baby.Id, Zone(), _time.GetLocalNow(), CancellationToken.None)
            : null;

        // An entry waiting for its delete (Spec 007 §5) is already gone from what the parent sees.
        if (_summary is not null)
            _summary = _summary with { TodayEntries = [.. _summary.TodayEntries.Where(e => !_undo.IsPendingDelete(e.Id))] };
        _sleep = _baby.IsBorn && _getSleep is not null
            ? await _getSleep.ExecuteAsync(_household.Id, _baby, Zone(), _time.GetLocalNow(), CancellationToken.None)
            : null;
        _siblingGlances = await GlancesAsync();
        _stash = _getStash is not null && KindVisibility.IsShown(_household, "pump")
            ? await _getStash.ExecuteAsync(_household.Id, _time.GetUtcNow(), CancellationToken.None)
            : null;
        _sleepRecord = _sleep is { IsPersonal: true } && _getSleep is not null
            ? await _getSleep.TrackRecordAsync(_household.Id, _baby, Zone(), _time.GetLocalNow(), CancellationToken.None)
            : null;
        _plan = _baby.IsBorn && _getPlan is not null
            ? await _getPlan.ExecuteAsync(_household.Id, _baby, Zone(), _time.GetLocalNow(), CancellationToken.None)
            : null;
        IsViewOnly = _logAccess is not null && !await _logAccess.CanLogAsync(_household.Id, CancellationToken.None);
        _attentionCount = (await _attention.ExecuteAsync(_household.Id, _time.GetLocalNow(), CancellationToken.None)).Count;
        _who = await _whoSource.ForAsync(_household.Id);
        _currentLocalDay = _time.GetLocalNow().Date;
        Relayout();
    }

    private DateTime? _currentLocalDay;

    /// <summary>Called each second by the page while visible: timers and "awake for" move on without a reload.</summary>
    public void Tick()
    {
        if (_household is null || _baby is null)
            return;
        var today = _time.GetLocalNow().Date;
        if (_currentLocalDay is not null && _currentLocalDay.Value != today)
        {
            _currentLocalDay = today;
            _ = RefreshAsync();
            return;
        }
        var snapshot = Snapshot();
        foreach (var section in Sections)
            section.Update(snapshot);
    }

    /// <summary>Called on app resume to detect overnight day transition.</summary>
    public void CheckDayChanged()
    {
        var today = _time.GetLocalNow().Date;
        if (_currentLocalDay is not null && _currentLocalDay.Value != today)
        {
            _currentLocalDay = today;
            _ = RefreshAsync();
        }
    }

    /// <summary>Whether the night palette shows, for the Today switch's glyph (Spec 009 §3.7).</summary>
    public bool IsNight => _theme.Current == PaletteKind.Night;

    /// <summary>The sun or moon on Today (Spec 009 §3.7).</summary>
    [RelayCommand]
    private Task ToggleTheme() => _theme.ToggleAsync(CancellationToken.None);

    [RelayCommand]
    private void CloseSheet()
    {
        _awakeCheck = null;
        Sheet = HomeSheet.None;
    }

    /// <summary>The answer to "woke up or still asleep?" (Spec 006 §5a).</summary>
    [RelayCommand]
    private async Task ResolveAwake(bool wokeUp)
    {
        if (_awakeCheck is not { } check)
            return;
        _awakeCheck = null;
        Sheet = HomeSheet.None;
        await _actions.ResolveAwakeCheckAsync(check, wokeUp);
    }

    [RelayCommand]
    private Task StartBreast(BreastSide side) => _actions.StartBreastAsync(side);

    [RelayCommand]
    private void OpenBottle()
    {
        PrefillBottle();
        Sheet = HomeSheet.Bottle;
    }

    /// <summary>
    /// The last bottle's amounts and milk (of the last week, even when a breastfeed came after it), or else what a baby of
    /// this age typically takes; also when the night row asks for the sheet.
    /// </summary>
    private void PrefillBottle()
    {
        var last = _summary?.LastBottle?.Details as BottleFeed;
        BottleAmount = last is null ? TypicalBottle() : ToUnit(last.Components[0].Volume);
        SecondAmount = last is { Components.Count: > 1 } ? ToUnit(last.Components[1].Volume) : BottleAmount;
        Milk = last is null ? BottleMilk.Formula : last.Components.Count > 1 ? BottleMilk.Both
            : last.Components[0].Milk == Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula ? BottleMilk.Formula : BottleMilk.Breast;
    }

    /// <summary>The side to offer first on the Feed sheet: the other one than the last breastfeed ended on.</summary>
    public BreastSide? SuggestedSide => _summary?.LastFeed?.Details is BreastFeed bf
        ? bf.LastSide == BreastSide.Left ? BreastSide.Right : BreastSide.Left
        : null;

    /// <summary>"Last feed 2 h 10 m ago · Right side" for the Feed sheet; empty before the first feed.</summary>
    public string LastFeedLine
    {
        get
        {
            if (_summary?.LastFeed is not { } feed)
                return "";
            var ago = _time.GetUtcNow() - feed.Timing.OccurredAt;
            var when = ago < TimeSpan.FromMinutes(1) ? Strings.Get("Feed.JustNow") : Strings.Format("Feed.Ago", Coo.Mobile.Presentation.Formatting.Formats.Duration(ago));
            var what = feed.Details switch
            {
                BreastFeed bf => Coo.Mobile.Presentation.Formatting.Formats.Side(bf.LastSide, shortForm: false),
                BottleFeed bottle => $"{Formats().Volume(bottle.Total)} · {Coo.Mobile.Presentation.Formatting.Formats.MilkName(bottle.Components)}",
                _ => "",
            };
            return Strings.Format("Feed.LastFeed", what.Length > 0 ? $"{when} · {what}" : when);
        }
    }

    /// <summary>"Last bottle 90 ml · Breast milk · 10:37" for the Bottle sheet; empty when there was none this week.</summary>
    public string LastBottleLine => _summary?.LastBottle is { Details: BottleFeed bottle } entry
        ? Strings.Format("Bottle.Last", $"{Formats().Volume(bottle.Total)} · {Coo.Mobile.Presentation.Formatting.Formats.MilkName(bottle.Components)} · {Formats().Time(entry.Timing.OccurredAt)}")
        : "";

    /// <summary>
    /// One-tap amounts around what this baby takes: the last bottle (or the typical one for the age) with a step of 30 ml
    /// (1 oz) either side, and one more above (half that step for small bottles), so most bottles are one tap instead of several on "+".
    /// </summary>
    public IReadOnlyList<decimal> QuickAmounts
    {
        get
        {
            var ounces = _household?.VolumeUnit == VolumeUnit.Ounces;
            var middle = _summary?.LastBottle?.Details is BottleFeed last ? ToUnit(last.Components[0].Volume) : TypicalBottle();
            // To 5 ml (or half an ounce), not to the ±10 ml step: 15 ml became 20, and the chips read 5, 20, 35, 50 with the
            // amount shown, 15, on none of them (owner's phone, 2026-11-03).
            var grain = ounces ? 0.5m : 5m;
            middle = Math.Max(grain, Math.Round(middle / grain, MidpointRounding.AwayFromZero) * grain);
            // Small bottles in small steps: a newborn goes 15, 30, 45 ml, not 15, 45, 75.
            var step = ounces ? (middle < 2m ? 0.5m : 1m) : (middle < 60m ? 15m : 30m);
            return [.. new[] { middle - step, middle, middle + step, middle + 2 * step }.Where(a => a > 0).Distinct()];
        }
    }

    [RelayCommand]
    private void SetAmount(decimal amount) => BottleAmount = Math.Max(Step, amount);

    private decimal TypicalBottle()
    {
        var ageDays = _baby?.BirthDate is { } born ? DateOnly.FromDateTime(Today).DayNumber - born.DayNumber : 0;
        return _household?.VolumeUnit == VolumeUnit.Ounces ? BottleSizes.TypicalOunces(ageDays) : BottleSizes.TypicalMillilitres(ageDays);
    }

    [RelayCommand]
    private void ChangeAmount(string direction) => BottleAmount = Math.Max(Step, BottleAmount + (direction == "+" ? Step : -Step));

    [RelayCommand]
    private void ChangeSecondAmount(string direction) => SecondAmount = Math.Max(Step, SecondAmount + (direction == "+" ? Step : -Step));

    [RelayCommand]
    private Task LogBottle()
    {
        var first = FromUnit(BottleAmount);
        if (first.IsFailure)
            return ShowError(first.Error!.Code);

        var milk = Milk == BottleMilk.Breast ? Coo.Domain.CareLog.Kinds.Feeding.Milk.ExpressedBreastMilk : Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula;
        List<BottleComponent> components = [new(milk, first.Value)];
        if (Milk == BottleMilk.Both)
        {
            var second = FromUnit(SecondAmount);
            if (second.IsFailure)
                return ShowError(second.Error!.Code);
            components = [new(Coo.Domain.CareLog.Kinds.Feeding.Milk.ExpressedBreastMilk, first.Value), new(Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula, second.Value)];
        }

        var bottle = BottleFeed.Create(components);
        return bottle.IsSuccess ? _actions.LogBottleAsync(bottle.Value, Formats()) : ShowError(bottle.Error!.Code);
    }

    [RelayCommand]
    private Task LogDiaper(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents contents) => _actions.LogDiaperAsync(contents);

    [RelayCommand]
    private Task SavePumpYield(bool skip)
    {
        if (_pumpForYield is not { } id)
            return Task.CompletedTask;
        if (skip)
            return _actions.EndSessionAsync(id);

        var left = ParseVolume(PumpLeft);
        var right = ParseVolume(PumpRight);
        if (left is null && right is null)
            return _actions.EndSessionAsync(id);

        var yield = PumpYield.BySide(left, right);
        return yield.IsSuccess ? _actions.EndSessionAsync(id, yield.Value) : ShowError(yield.Error!.Code);
    }

    [RelayCommand]
    private async Task SaveBirth()
    {
        if (_baby is null)
            return;
        var result = await _recordBirth.ExecuteAsync(_baby, DateOnly.FromDateTime(BirthDate), CancellationToken.None);
        Sheet = HomeSheet.None;
        Toast = result.IsSuccess ? Strings.Format("Birth.Title", _baby.Name) : Strings.Error(result.Error?.Code);
        await RefreshAsync();
    }

    private void OpenSheet(HomeSheet sheet, EntryId? entry)
    {
        if (sheet == HomeSheet.PumpYield)
        {
            _pumpForYield = entry;
            (PumpLeft, PumpRight) = ("", "");
        }

        if (sheet == HomeSheet.RecordBirth)
            BirthDate = _time.GetLocalNow().Date;

        if (sheet == HomeSheet.Bottle)
            PrefillBottle();

        Sheet = sheet;
    }

    private void Relayout()
    {
        if (_household is null || _baby is null)
            return;
        var visible = _all.Where(s => s.AppliesTo(Stage, _theme.Current) && !(IsViewOnly && s is ILoggingSection)).ToList();
        if (!visible.SequenceEqual(Sections))
        {
            Sections.Clear();
            foreach (var section in visible)
                Sections.Add(section);
        }

        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(IsNight));
        Tick();
    }

    private HomeSnapshot Snapshot() => new(_household!, _baby!, _summary, _time.GetLocalNow(), _theme.Current, Formats(), _attentionCount, _who, _billing.IsPremiumCached, _avatarRef, _sleep, IsViewOnly, _plan, _sleepRecord, _siblingGlances, _logForAll, _stash);

    private Formats Formats() => new(_clock.Is24Hour, _household?.VolumeUnit ?? VolumeUnit.Millilitres);

    private TimeZoneInfo Zone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(_household!.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return _time.LocalTimeZone;
        }
    }

    /// <summary>To half an ounce, or to 5 ml: to 10 ml, a newborn's 15 ml bottle came back as 20.</summary>
    private decimal ToUnit(Volume volume) =>
        _household?.VolumeUnit == VolumeUnit.Ounces
            ? Math.Round(volume.Ounces * 2, MidpointRounding.AwayFromZero) / 2
            : Math.Round(volume.Millilitres / 5, MidpointRounding.AwayFromZero) * 5;

    private Result<Volume> FromUnit(decimal amount) =>
        _household?.VolumeUnit == VolumeUnit.Ounces ? Volume.FromOunces(amount) : Volume.FromMillilitres(amount);

    private Volume? ParseVolume(string text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number, Strings.Culture, out var n) && FromUnit(n) is { IsSuccess: true } v ? v.Value : null;

    private Task ShowError(string code)
    {
        Toast = Strings.Error(code);
        return Task.CompletedTask;
    }
}
