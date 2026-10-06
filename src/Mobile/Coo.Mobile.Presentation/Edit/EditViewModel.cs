using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.Common;
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
    ICareLogStore store, IHouseholdStore households, LogEntry log, ReviseEntry revise, UndoService undo, IClockFormat clock, TimeProvider time)
    : ObservableObject
{
    private CareEntry? _original;
    private DateTimeOffset _loadedStart;
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

    public DateTime Today => DateTime.SpecifyKind(time.GetLocalNow().Date, DateTimeKind.Unspecified);

    /// <summary>Raised when the form is done (saved or deleted); the host closes it.</summary>
    public event EventHandler? Closed;

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
        var at = time.GetLocalNow().AddMinutes(-15);
        SetStart(at);
        SetEnd(at.AddMinutes(kindCode == "sleep" ? 15 : 10));
        HasEnd = true;
        (LeftMinutes, RightMinutes, Amount, SecondAmount, Note, Error, Warnings) = ("", "", "", "", "", "", "");
        Amount = _household?.VolumeUnit == VolumeUnit.Ounces ? "4" : "120";
        SecondAmount = Amount;
    }

    [RelayCommand]
    private async Task Save()
    {
        var built = Build();
        if (built.IsFailure)
        {
            Error = Strings.Error(built.Error!.Code);
            return;
        }

        var (details, timing, note) = built.Value;
        var result = IsNew ? await CreateAsync(details, timing, note) : await UpdateAsync(details, timing, note);
        if (result.IsFailure)
        {
            Error = Strings.Error(result.Error!.Code);
            return;
        }

        Error = "";
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

    /// <summary>Each changed part is its own revision with its own stamp from the clock (Spec 007 §3).</summary>
    private async Task<Result> UpdateAsync(EntryDetails details, EntryTiming? timing, Note? note)
    {
        // ReviseEntry reloads the entry from storage for each change, so _original keeps the values as they were:
        // it is the snapshot that Undo puts back.
        var before = _original!;
        var derived = before.Kind.TimingFor(details) == TimingRule.DerivedFromDetails;
        var after = before;
        var current = before.Details;

        if (!IsInParts && details is BreastFeed
            && await store.FindAsync(before.Id, CancellationToken.None) is LiveEntry { Entry.Details: BreastFeed { IsCompleted: true, Parts: > 1 } merged })
        {
            // Merged on the other phone while this form was open (Spec 011d §5): the form cannot express the parts, so it
            // keeps every segment; only a move of the start applies, to all of them.
            var shift = Combine(StartDate, StartTime) - Combine(Local(_loadedStart).Date, Local(_loadedStart).TimeOfDay);
            var moved = merged.WithSegments(merged.Segments.Select(s => s with { Start = s.Start + shift, End = s.End + shift }));
            if (moved.IsFailure)
                return Result.Failure(moved.Error!);
            (current, details) = (merged, moved.Value);
        }

        if (!current.Equals(details))
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, now) => e.Revise<EntryDetails>(_ => Result<EntryDetails>.Ok(details), v, now), CancellationToken.None);
            if (r.IsFailure)
                return r;
            after = r.Value;
        }

        if (!derived && timing is not null && timing != before.Timing)
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, now) => e.Retime(timing, v, now), CancellationToken.None);
            if (r.IsFailure)
                return r;
            after = r.Value;
        }

        if (note != before.Note)
        {
            var r = await revise.ExecuteAsync(before.Id, (e, v, _) => e.SetNote(note, v), CancellationToken.None);
            if (r.IsFailure)
                return r;
            after = r.Value;
        }

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
        _loadedStart = entry.Timing.OccurredAt;
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
    }

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
        d <= TimeSpan.Zero ? "" : Math.Max(1, (int)Math.Round(d.TotalMinutes)).ToString(Strings.Culture);

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
        _ => "Kind.Pump",
    };
}
