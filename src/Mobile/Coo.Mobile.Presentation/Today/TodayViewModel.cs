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

    public TodayViewModel(
        IEnumerable<IHomeSection> sections, IHouseholdStore households, GetToday getToday, HomeActions actions,
        ThemeController theme, IClockFormat clock, RecordBirth recordBirth, TimeProvider time)
    {
        _all = [.. sections.OrderBy(s => s.Order)];
        (_households, _getToday, _actions, _theme, _clock, _recordBirth, _time) = (households, getToday, actions, theme, clock, recordBirth, time);
        _actions.Done += async (_, outcome) =>
        {
            Sheet = HomeSheet.None;
            Toast = outcome.Message;
            await RefreshAsync();
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

    public HomeStage Stage => _baby?.IsBorn == false ? HomeStage.Expecting : HomeStage.Born;

    /// <summary>True when the household has no baby yet: the host shows setup (Spec 006 §3).</summary>
    public bool NeedsSetup { get; private set; }

    public string UnitLabel => Strings.Get(_household?.VolumeUnit == VolumeUnit.Ounces ? "Unit.Oz" : "Unit.Ml");

    private decimal Step => _household?.VolumeUnit == VolumeUnit.Ounces ? 0.5m : 10m;

    /// <summary>Loads everything from the store. Called on appearing and after every action.</summary>
    public async Task RefreshAsync()
    {
        _household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (_household is null)
            return;

        var babies = await _households.ListBabiesAsync(_household.Id, CancellationToken.None);
        _baby = babies.Count > 0 ? babies[0] : null;
        NeedsSetup = _baby is null;
        OnPropertyChanged(nameof(NeedsSetup));
        if (_baby is null)
            return;

        _actions.Context = new HomeContext(_household.Id, _baby.Id);
        _summary = _baby.IsBorn
            ? await _getToday.ExecuteAsync(_household.Id, _baby.Id, Zone(), _time.GetLocalNow(), CancellationToken.None)
            : null;
        Relayout();
    }

    /// <summary>Called each second by the page while visible: timers and "awake for" move on without a reload.</summary>
    public void Tick()
    {
        if (_household is null || _baby is null)
            return;
        var snapshot = Snapshot();
        foreach (var section in Sections)
            section.Update(snapshot);
    }

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
        var last = _summary?.LastFeed?.Details as BottleFeed;
        BottleAmount = last is null ? (_household?.VolumeUnit == VolumeUnit.Ounces ? 4m : 120m) : ToUnit(last.Components[0].Volume);
        SecondAmount = last is { Components.Count: > 1 } ? ToUnit(last.Components[1].Volume) : BottleAmount;
        Milk = last is null ? BottleMilk.Formula : last.Components.Count > 1 ? BottleMilk.Both
            : last.Components[0].Milk == Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula ? BottleMilk.Formula : BottleMilk.Breast;
        Sheet = HomeSheet.Bottle;
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

        Sheet = sheet;
    }

    private void Relayout()
    {
        if (_household is null || _baby is null)
            return;
        var visible = _all.Where(s => s.AppliesTo(Stage, _theme.Current)).ToList();
        if (!visible.SequenceEqual(Sections))
        {
            Sections.Clear();
            foreach (var section in visible)
                Sections.Add(section);
        }

        OnPropertyChanged(nameof(Stage));
        Tick();
    }

    private HomeSnapshot Snapshot() => new(_household!, _baby!, _summary, _time.GetLocalNow(), _theme.Current, Formats());

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

    private decimal ToUnit(Volume volume) =>
        _household?.VolumeUnit == VolumeUnit.Ounces ? Math.Round(volume.Ounces * 2, MidpointRounding.AwayFromZero) / 2 : Math.Round(volume.Millilitres / 10) * 10;

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
