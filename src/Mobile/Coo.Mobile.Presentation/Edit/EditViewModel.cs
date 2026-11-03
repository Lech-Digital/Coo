using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Development;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Edit;

/// <summary>
/// One entry's form (Spec 007 §3, §4): editing an entry, or logging one that already happened. Every rule is the
/// Domain's; errors and plausibility warnings are shown inline, in the user's language. Warnings never block Save.
/// </summary>
public sealed partial class EditViewModel(
    ICareLogStore store, IHouseholdStore households, LogEntry log, ReviseEntry revise, ResubmitRejectedEntry resubmit, UndoService undo,
    IClockFormat clock, TimeProvider time, IReviewPrompter reviewPrompter)
    : ObservableObject
{
    private CareEntry? _original;
    private HlcStamp? _loadedVersion;
    private IReadOnlyList<string> _partLines = [];
    private bool _loading;
    private HouseholdSettings? _household;
    private BabyProfile? _baby;

    [ObservableProperty]
    private string _kindCode = "diaper";

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isReadable = true;

    [ObservableProperty]
    private string _unreadableText = "";

    [ObservableProperty]
    private DateTime _startDate;

    [ObservableProperty]
    private TimeSpan _startTime;

    [ObservableProperty]
    private bool _hasEnd = true;

    [ObservableProperty]
    private DateTime _endDate;

    [ObservableProperty]
    private TimeSpan _endTime;

    [ObservableProperty]
    private BreastSide _firstSide = BreastSide.Left;

    [ObservableProperty]
    private string _leftMinutes = "";

    [ObservableProperty]
    private string _rightMinutes = "";

    [ObservableProperty]
    private BottleMilk _milk = BottleMilk.Formula;

    [ObservableProperty]
    private string _amount = "";

    [ObservableProperty]
    private string _secondAmount = "";

    [ObservableProperty]
    private DiaperContents _contents = DiaperContents.Wet;

    [ObservableProperty]
    private SleepKindOverride? _sleepKind;

    [ObservableProperty]
    private string _pumpLeft = "";

    [ObservableProperty]
    private string _pumpRight = "";

    [ObservableProperty]
    private string _weight = "";

    [ObservableProperty]
    private string _length = "";

    [ObservableProperty]
    private string _head = "";

    /// <summary>A medicine's or a vaccine's name.</summary>
    [ObservableProperty]
    private string _healthName = "";

    /// <summary>A medicine's dose, or who a visit was with.</summary>
    [ObservableProperty]
    private string _healthDetail = "";

    [ObservableProperty]
    private string _temperature = "";

    /// <summary>How a solid food went; the food is in <see cref="HealthName"/>, the amount in <see cref="HealthDetail"/>.</summary>
    [ObservableProperty]
    private SolidsReaction _solidsReaction = SolidsReaction.None;

    /// <summary>A milestone's preset code, or <see cref="MilestoneDetails.Custom"/>; its own title is in <see cref="HealthName"/>.</summary>
    [ObservableProperty]
    private string _milestoneCode = MilestoneDetails.Presets[0];

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _warnings = "";

    public EntryId? EntryId { get; private set; }

    /// <summary>
    /// A finished breast feed in two or more parts (Spec 011d §5). The form cannot edit single parts, so it lists them
    /// read-only (<see cref="PartLines"/>); moving the start moves every segment, and the note edits as usual.
    /// </summary>
    public bool IsInParts => PartLines.Count > 0;

    /// <summary>"Part 1 · 14:02–14:12 · left", one line per part; empty unless <see cref="IsInParts"/>.</summary>
    public IReadOnlyList<string> PartLines
    {
        get => _partLines;
        private set
        {
            if (SetProperty(ref _partLines, value))
                OnPropertyChanged(nameof(IsInParts));
        }
    }

    public string UnitLabel => Strings.Get(_household?.VolumeUnit == VolumeUnit.Ounces ? "Unit.Oz" : "Unit.Ml");

    /// <summary>Households counting in ounces measure in pounds, inches and °F; the rest in kg, cm and °C.</summary>
    public bool IsImperial => _household?.VolumeUnit == VolumeUnit.Ounces;

    public string WeightUnit => Strings.Get(IsImperial ? "Unit.Lb" : "Unit.Kg");

    public string LengthUnit => Strings.Get(IsImperial ? "Unit.In" : "Unit.Cm");

    public string TemperatureUnit => IsImperial ? "°F" : "°C";

    public DateTime Today => DateTime.SpecifyKind(time.GetLocalNow().Date, DateTimeKind.Unspecified);

    /// <summary>Raised when the form is done (saved or deleted); the host closes it.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised when Save found the entry changed on the other phone and filled the form again: the host redraws it.</summary>
    public event EventHandler? Reloaded;

    /// <summary>Opens an existing entry. Read-only and quarantined entries open as an info form with Delete.</summary>
    public async Task LoadAsync(EntryId id)
    {
        _loading = true;
        try
        {
            await LoadAsyncCore(id);
        }
        finally
        {
            _loading = false;
        }

        RecomputeWarnings();
    }

    private async Task LoadAsyncCore(EntryId id)
    {
        await LoadContextAsync();
        EntryId = id;
        IsNew = false;
        PartLines = [];
        var stored = await store.FindAsync(id, CancellationToken.None);
        switch (stored)
        {
            case LiveEntry { Entry: var entry }:
                _original = entry;
                Fill(entry);
                break;
            case ReadOnlyEntry r:
                (IsReadable, KindCode, UnreadableText) = (false, r.KindCode, Strings.Get("Edit.Newer"));
                SetStart(r.Start);
                break;
            case QuarantinedEntry q:
                (IsReadable, KindCode, UnreadableText) = (false, q.KindCode, Strings.Get("Edit.Unreadable"));
                SetStart(q.Start);
                break;
            default:
                Error = Strings.Error(ApplicationErrors.EntryNotFound);
                break;
        }
    }

    /// <summary>"Earlier…" (Spec 007 §4): an empty form for a kind, set to 15 minutes ago (Q6).</summary>
    public async Task NewAsync(string kindCode)
    {
        _loading = true;
        try
        {
            await NewAsyncCore(kindCode);
        }
        finally
        {
            _loading = false;
        }

        RecomputeWarnings();
    }

    private async Task NewAsyncCore(string kindCode)
    {
        await LoadContextAsync();
        (IsNew, KindCode, EntryId, _original, PartLines) = (true, kindCode, null, null, []);
        // A feed or a nap logged afterwards started a while ago (Q6); a dose, a reading or a measurement is usually now.
        var at = kindCode is "growth" or "medicine" or "temperature" or "vaccine" or "visit" or "bath" or "solids" or "milestone" ? time.GetLocalNow() : time.GetLocalNow().AddMinutes(-15);
        SetStart(at);
        SetEnd(at.AddMinutes(kindCode == "sleep" ? 15 : 10));
        HasEnd = true;
        (LeftMinutes, RightMinutes, Amount, SecondAmount, Note, Error, Warnings) = ("", "", "", "", "", "", "");
        (Weight, Length, Head, HealthName, HealthDetail, Temperature) = ("", "", "", "", "", "");
        (SolidsReaction, MilestoneCode) = (SolidsReaction.None, MilestoneDetails.Presets[0]);
        Amount = _household?.VolumeUnit == VolumeUnit.Ounces ? "4" : "120";
        SecondAmount = Amount;
    }

    [RelayCommand]
    private async Task Save()
    {
        var stored = IsNew ? null : await store.FindAsync(EntryId!.Value, CancellationToken.None);
        if (stored is LiveEntry { Entry: var changed } && changed.Version != _loadedVersion)
        {
            // Changed on the other phone since the form loaded (a merge, a note, a move): the form's values are stale and
            // would undo that change, so nothing is written; the form shows the entry as it is now (Spec 011d §5).
            await LoadAsync(changed.Id);
            Error = Strings.Get("Edit.ChangedElsewhere");
            Reloaded?.Invoke(this, EventArgs.Empty);
            return;
        }

        var built = Build();
        if (built.IsFailure)
        {
            Error = Strings.Error(built.Error!.Code);
            return;
        }

        var (details, timing, note) = built.Value;
        var result = IsNew ? await CreateAsync(details, timing, note)
            : stored is LiveEntry { Entry: var before } ? await UpdateAsync(before, details, timing, note)
            : Result.Failure(stored is null ? ApplicationErrors.EntryNotFound : ApplicationErrors.EntryNotEditable);
        if (result.IsFailure)
        {
            Error = Strings.Error(result.Error!.Code);
            return;
        }

        Error = "";
        _ = reviewPrompter.CheckAndPromptAsync(CancellationToken.None);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (EntryId is not { } id)
            return;
        var what = _original is null ? Strings.Get(KindName(KindCode)) : Formats().Title(_original, time.GetLocalNow());
        await undo.DeleteWithUndoAsync(id, what);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Warnings) or nameof(Error) or nameof(IsNew) or nameof(KindCode) or nameof(IsReadable) or nameof(UnreadableText)
            or nameof(PartLines) or nameof(IsInParts))
            return;
        RecomputeWarnings();
    }

    private async Task<Result> CreateAsync(EntryDetails details, EntryTiming? timing, Note? note)
    {
        var subject = KindCode == "pump" ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(_baby!.Id);
        var created = await log.ExecuteAsync(
            new LogEntryCommand(_household!.Id, subject, details, timing, PersonRef.ThisDevicesPerson, EntrySource.Manual, note), CancellationToken.None);
        if (created.IsSuccess)
            undo.OfferUndoForLog(created.Value.Id, Strings.Format("Toast.Logged", Formats().Title(created.Value, time.GetLocalNow())));
        return created;
    }

    /// <summary>
    /// Each changed part is its own revision with its own stamp from the clock (Spec 007 §3). <paramref name="before"/> is
    /// the entry as stored right before the save, at the version the form loaded: it is what Undo puts back. A rejected
    /// entry saved with no change is written again with a new version, so it is pushed again.
    /// </summary>
    private async Task<Result> UpdateAsync(CareEntry before, EntryDetails details, EntryTiming? timing, Note? note)
    {
        // ReviseEntry reloads the entry from storage for each change, so before keeps the values as they were.
        var derived = before.Kind.TimingFor(details) == TimingRule.DerivedFromDetails;
        var after = before;
        var wrote = false;

        if (!before.Details.Equals(details))
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, now) => e.Revise<EntryDetails>(_ => Result<EntryDetails>.Ok(details), v, now), CancellationToken.None);
            if (r.IsFailure)
                return r;
            (after, wrote) = (r.Value, true);
        }

        if (!derived && timing is not null && timing != before.Timing)
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, now) => e.Retime(timing, v, now), CancellationToken.None);
            if (r.IsFailure)
                return r;
            (after, wrote) = (r.Value, true);
        }

        if (note != before.Note)
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, _) => e.SetNote(note, v), CancellationToken.None);
            if (r.IsFailure)
                return r;
            (after, wrote) = (r.Value, true);
        }

        if (!wrote && await resubmit.ExecuteAsync(before.Id, CancellationToken.None) is { } resubmitted)
            after = resubmitted;

        // The toast names the entry as it is now: "Saved · Diaper · Mixed".
        undo.OfferUndoForEdit(before, Strings.Format("Toast.Saved", Formats().Title(after, time.GetLocalNow())));
        return Result.Success;
    }

    /// <summary>The form's values as Domain objects, through the Domain factories: every rule is checked there.</summary>
    private Result<(EntryDetails Details, EntryTiming? Timing, Note? Note)> Build()
    {
        var note = Coo.Domain.CareLog.Note.From(Note);
        if (note.IsFailure)
            return Result<(EntryDetails, EntryTiming?, Note?)>.Failure(note.Error!);

        var start = Combine(StartDate, StartTime);
        DateTimeOffset? end = HasEnd && !IsRunning ? Combine(EndDate, EndTime) : null;

        Result<(EntryDetails, EntryTiming?, Note?)> Ok(EntryDetails d, EntryTiming? t) => Result<(EntryDetails, EntryTiming?, Note?)>.Ok((d, t, note.Value));
        Result<(EntryDetails, EntryTiming?, Note?)> Fail(DomainError e) => Result<(EntryDetails, EntryTiming?, Note?)>.Failure(e);

        switch (KindCode)
        {
            case "feeding" when IsBottle:
                {
                    var first = Volume(Amount);
                    if (first.IsFailure)
                        return Fail(first.Error!);
                    List<BottleComponent> parts = Milk switch
                    {
                        BottleMilk.Breast => [new(Coo.Domain.CareLog.Kinds.Feeding.Milk.ExpressedBreastMilk, first.Value)],
                        BottleMilk.Formula => [new(Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula, first.Value)],
                        _ => [],
                    };
                    if (Milk == BottleMilk.Both)
                    {
                        var second = Volume(SecondAmount);
                        if (second.IsFailure)
                            return Fail(second.Error!);
                        parts = [new(Coo.Domain.CareLog.Kinds.Feeding.Milk.ExpressedBreastMilk, first.Value), new(Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula, second.Value)];
                    }

                    var bottle = BottleFeed.Create(parts);
                    return bottle.IsSuccess ? Ok(bottle.Value, EntryTiming.Point(start)) : Fail(bottle.Error!);
                }

            case "feeding":
                {
                    if (!IsRunning && IsInParts && _original?.Details is BreastFeed parted)
                    {
                        // A feed in parts keeps every segment: moving the start shifts them all, never flattens them.
                        var shift = start - Combine(Local(parted.FirstStart).Date, Local(parted.FirstStart).TimeOfDay);
                        var moved = parted.WithSegments(parted.Segments.Select(s => s with { Start = s.Start + shift, End = s.End + shift }));
                        return moved.IsSuccess ? Ok(moved.Value, null) : Fail(moved.Error!);
                    }

                    if (IsRunning && _original?.Details is BreastFeed running)
                    {
                        // A running feed: only its start moves (Spec 007 §2).
                        var shifted = running.Segments.Select((s, i) => i == 0 ? s with { Start = start } : s).ToList();
                        var moved = running.WithSegments(shifted);
                        return moved.IsSuccess ? Ok(moved.Value, null) : Fail(moved.Error!);
                    }

                    var sides = new List<(BreastSide, int)>();
                    var other = FirstSide == BreastSide.Left ? BreastSide.Right : BreastSide.Left;
                    foreach (var (side, text) in new[] { (FirstSide, FirstSide == BreastSide.Left ? LeftMinutes : RightMinutes), (other, other == BreastSide.Left ? LeftMinutes : RightMinutes) })
                    {
                        if (string.IsNullOrWhiteSpace(text))
                            continue;
                        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, Strings.Culture, out var minutes))
                            return Fail(DomainError.Of(FeedingErrors.BreastSideDurationInvalid));
                        if (minutes > 0)
                            sides.Add((side, minutes));
                    }

                    var feed = BreastFeed.FromSideDurations(start, sides);
                    return feed.IsSuccess ? Ok(feed.Value, null) : Fail(feed.Error!);
                }

            case "diaper":
                return Ok(new DiaperDetails(Contents), EntryTiming.Point(start));

            case "sleep":
                {
                    var range = TimeRange.Create(start, end);
                    return range.IsSuccess ? Ok(new SleepDetails(SleepKind), EntryTiming.Session(range.Value)) : Fail(range.Error!);
                }

            case "growth":
                {
                    if (!TryMeasure(Weight, IsImperial ? GramsPerPound : 1000m, out var grams)
                        || !TryMeasure(Length, IsImperial ? MmPerInch : 10m, out var length)
                        || !TryMeasure(Head, IsImperial ? MmPerInch : 10m, out var head))
                        return Fail(DomainError.Of(NumberInvalid));
                    var growth = GrowthDetails.Create(grams, length, head);
                    return growth.IsSuccess ? Ok(growth.Value, EntryTiming.Point(start)) : Fail(growth.Error!);
                }

            case "medicine":
                {
                    var medicine = MedicineDetails.Create(HealthName, HealthDetail);
                    return medicine.IsSuccess ? Ok(medicine.Value, EntryTiming.Point(start)) : Fail(medicine.Error!);
                }

            case "temperature":
                {
                    if (!decimal.TryParse(Temperature, System.Globalization.NumberStyles.Number, Strings.Culture, out var degrees))
                        return Fail(DomainError.Of(NumberInvalid));
                    var celsius = IsImperial ? (degrees - 32m) * 5m / 9m : degrees;
                    var reading = TemperatureDetails.Create(celsius);
                    return reading.IsSuccess ? Ok(reading.Value, EntryTiming.Point(start)) : Fail(reading.Error!);
                }

            case "vaccine":
                {
                    var vaccine = VaccineDetails.Create(HealthName);
                    return vaccine.IsSuccess ? Ok(vaccine.Value, EntryTiming.Point(start)) : Fail(vaccine.Error!);
                }

            case "visit":
                {
                    var visit = VisitDetails.Create(HealthDetail);
                    return visit.IsSuccess ? Ok(visit.Value, EntryTiming.Point(start)) : Fail(visit.Error!);
                }

            case "bath":
                return Ok(new Coo.Domain.CareLog.Kinds.Bath.BathDetails(), EntryTiming.Point(start));

            case "tummy":
                {
                    var range = TimeRange.Create(start, end);
                    return range.IsSuccess ? Ok(new TummyDetails(), EntryTiming.Session(range.Value)) : Fail(range.Error!);
                }

            case "solids":
                {
                    var solids = SolidsDetails.Create(HealthName, HealthDetail, SolidsReaction);
                    return solids.IsSuccess ? Ok(solids.Value, EntryTiming.Point(start)) : Fail(solids.Error!);
                }

            case "milestone":
                {
                    var milestone = MilestoneDetails.Create(MilestoneCode, MilestoneCode == MilestoneDetails.Custom ? HealthName : null);
                    return milestone.IsSuccess ? Ok(milestone.Value, EntryTiming.Point(start)) : Fail(milestone.Error!);
                }

            default:
                {
                    var range = TimeRange.Create(start, end);
                    if (range.IsFailure)
                        return Fail(range.Error!);
                    var left = OptionalVolume(PumpLeft);
                    var right = OptionalVolume(PumpRight);
                    if (left is { IsFailure: true })
                        return Fail(left.Error!);
                    if (right is { IsFailure: true })
                        return Fail(right.Error!);
                    PumpYield? yield = null;
                    if (left is not null || right is not null)
                    {
                        var bySide = PumpYield.BySide(left?.Value, right?.Value);
                        if (bySide.IsFailure)
                            return Fail(bySide.Error!);
                        yield = bySide.Value;
                    }

                    return Ok(new PumpDetails(yield), EntryTiming.Session(range.Value));
                }
        }
    }

    /// <summary>True for a new feeding form that the host opened as "bottle".</summary>
    public bool IsBottleForm { get; set; }

    /// <summary>A feeding form is a bottle when it edits a bottle, or when a new form was opened as one.</summary>
    public bool IsBottle => IsNew ? IsBottleForm : _original?.Details is BottleFeed;

    private void RecomputeWarnings()
    {
        // While a form loads, its fields are half set (a start date of 0001-01-01 is no valid time east of UTC).
        if (_loading || _household is null || _baby is null || !IsReadable)
            return;
        var built = Build();
        if (built.IsFailure)
        {
            Warnings = "";
            return;
        }

        var (details, timing, _) = built.Value;
        var kind = EntryKinds.For(details.GetType());
        var resolvedTiming = timing ?? kind!.DeriveTiming(details);
        var subject = KindCode == "pump" ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(_baby.Id);
        var context = new PlausibilityContext(time.GetLocalNow()) { BabyBirthDate = _baby.BirthDate };
        var found = PlausibilityCheck.For(kind!, subject, details, resolvedTiming, context);
        Warnings = string.Join("\n", found.Select(w => w.Code == PlausibilityCodes.BeforeBirth.Code
            ? Strings.Format("Warn.before-birth", _baby.Name)
            : Strings.Get("Warn." + w.Code["warn.".Length..])));
    }

    private void Fill(CareEntry entry)
    {
        _loadedVersion = entry.Version;
        PartLines = entry.Details is BreastFeed { IsCompleted: true, Parts: > 1 } parted ? Lines(parted) : [];
        KindCode = entry.KindCode.Value;
        IsRunning = entry.Timing.IsInProgress;
        Note = entry.Note?.Text ?? "";
        SetStart(entry.Timing.OccurredAt);
        if (entry.Timing is SessionTiming { Range.End: { } e })
            SetEnd(e);
        else
            SetEnd(entry.Timing.OccurredAt.AddMinutes(30));
        HasEnd = entry.Timing is not SessionTiming { Range.IsOpen: true };

        switch (entry.Details)
        {
            case BreastFeed f:
                FirstSide = f.Segments[0].Side;
                LeftMinutes = Minutes(f.LeftDuration);
                RightMinutes = Minutes(f.RightDuration);
                break;
            case BottleFeed b:
                Milk = b.Components.Count > 1 ? BottleMilk.Both : b.Components[0].Milk == Coo.Domain.CareLog.Kinds.Feeding.Milk.Formula ? BottleMilk.Formula : BottleMilk.Breast;
                Amount = InUnit(b.Components[0].Volume);
                SecondAmount = b.Components.Count > 1 ? InUnit(b.Components[1].Volume) : Amount;
                break;
            case DiaperDetails d:
                Contents = d.Contents;
                break;
            case SleepDetails s:
                SleepKind = s.KindOverride;
                break;
            case PumpDetails { Yield: PumpYieldBySide y }:
                PumpLeft = y.Left is { } l ? InUnit(l) : "";
                PumpRight = y.Right is { } r ? InUnit(r) : "";
                break;
            case PumpDetails { Yield: PumpYieldTotal t }:
                PumpLeft = InUnit(t.Amount);
                break;
            case GrowthDetails g:
                Weight = Shown(g.WeightGrams, IsImperial ? GramsPerPound : 1000m, IsImperial ? "0.##" : "0.###");
                Length = Shown(g.LengthMm, IsImperial ? MmPerInch : 10m, "0.#");
                Head = Shown(g.HeadMm, IsImperial ? MmPerInch : 10m, "0.#");
                break;
            case MedicineDetails m:
                (HealthName, HealthDetail) = (m.Name, m.Dose ?? "");
                break;
            case TemperatureDetails t:
                Temperature = (IsImperial ? t.Celsius * 9m / 5m + 32m : t.Celsius).ToString("0.0", Strings.Culture);
                break;
            case VaccineDetails v:
                HealthName = v.Name;
                break;
            case VisitDetails v:
                HealthDetail = v.With ?? "";
                break;
            case SolidsDetails f:
                (HealthName, HealthDetail, SolidsReaction) = (f.Food, f.Amount ?? "", f.Reaction);
                break;
            case MilestoneDetails m:
                (MilestoneCode, HealthName) = (m.Code, m.Title ?? "");
                break;
        }
    }

    private async Task LoadContextAsync()
    {
        _household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (_household is not null)
        {
            var babies = await households.ListBabiesAsync(_household.Id, CancellationToken.None);
            _baby = babies.Count > 0 ? babies[0] : null;
        }

        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(IsImperial));
    }

    /// <summary>A measurement that is not a number ("4,5,1", "abc").</summary>
    public const string NumberInvalid = "edit.number-invalid";

    private const decimal GramsPerPound = 453.59237m;
    private const decimal MmPerInch = 25.4m;

    /// <summary>
    /// A measurement typed in the household's unit, in grams or millimetres: empty is "not measured" (null); anything
    /// unreadable fails. The Domain checks the range.
    /// </summary>
    private static bool TryMeasure(string text, decimal factor, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, Strings.Culture, out var n) || n <= 0)
            return false;
        value = (int)Math.Round(n * factor, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string Shown(int? stored, decimal factor, string format) =>
        stored is { } v ? (v / factor).ToString(format, Strings.Culture) : "";

    // The form shows wall-clock time in the TimeProvider's zone, never the machine's: CI runs in UTC, the phone does not.
    private void SetStart(DateTimeOffset at) => (StartDate, StartTime) = (Local(at).Date, Local(at).TimeOfDay);

    private void SetEnd(DateTimeOffset at) => (EndDate, EndTime) = (Local(at).Date, Local(at).TimeOfDay);

    private DateTime Local(DateTimeOffset at) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).DateTime, DateTimeKind.Unspecified);

    private DateTimeOffset Combine(DateTime date, TimeSpan clock)
    {
        var local = DateTime.SpecifyKind(date.Date + new TimeSpan(clock.Hours, clock.Minutes, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, time.LocalTimeZone.GetUtcOffset(local));
    }

    private Result<Volume> Volume(string text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number, Strings.Culture, out var n)
            ? (_household?.VolumeUnit == VolumeUnit.Ounces ? Coo.Domain.CareLog.Volume.FromOunces(n) : Coo.Domain.CareLog.Volume.FromMillilitres(n))
            : Result<Volume>.Failure(Coo.Domain.CareLog.Volume.OutOfRange);

    private Result<Volume>? OptionalVolume(string text) => string.IsNullOrWhiteSpace(text) ? null : Volume(text);

    private string InUnit(Volume v) => _household?.VolumeUnit == VolumeUnit.Ounces
        ? Math.Round(v.Ounces, 1).ToString("0.#", Strings.Culture)
        : Math.Round(v.Millilitres).ToString("0", Strings.Culture);

    private static string Minutes(TimeSpan d) =>
        d <= TimeSpan.Zero ? "" : Math.Max(1, (int)Math.Round(d.TotalMinutes, MidpointRounding.AwayFromZero)).ToString(Strings.Culture);

    private Formats Formats() => new(clock.Is24Hour, _household?.VolumeUnit ?? VolumeUnit.Millilitres);

    /// <summary>"Part 1 · 14:02–14:12 · left", "Part 2 · 14:20–14:31 · left, right": each part's times and sides, in order.</summary>
    private List<string> Lines(BreastFeed feed)
    {
        var formats = Formats();
        return [.. FeedParts.Of(feed).Select((part, i) => string.Join(" · ",
            Strings.Format("Edit.Part", i + 1),
            $"{formats.Time(part[0].Start, time.LocalTimeZone)}–{formats.Time(part[^1].End ?? part[^1].Start, time.LocalTimeZone)}",
            string.Join(", ", part.Select(s => s.Side).Distinct().Select(side => Formatting.Formats.Side(side, shortForm: false)))))];
    }

    private static string KindName(string code) => code switch
    {
        "feeding" => "Kind.Feed",
        "diaper" => "Kind.Diaper",
        "sleep" => "Kind.Sleep",
        "growth" => "Kind.Growth",
        "medicine" => "Kind.Medicine",
        "temperature" => "Kind.Temperature",
        "vaccine" => "Kind.Vaccine",
        "visit" => "Kind.Visit",
        "bath" => "Kind.Bath",
        "tummy" => "Kind.Tummy",
        "solids" => "Kind.Solids",
        "milestone" => "Kind.Milestone",
        "sleepplan" => "Kind.SleepPlan",
        _ => "Kind.Pump",
    };
}
