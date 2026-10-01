using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Today;

/// <summary>The household and baby that Today acts on.</summary>
public sealed record HomeContext(HouseholdId Household, BabyId Baby);

/// <summary>The sheet Today shows, if any (Spec 006 §5). Sheets are in-page overlays, never modal pages (§2).</summary>
public enum HomeSheet
{
    None,
    Feed,
    Bottle,
    Diaper,
    PumpYield,
    RecordBirth,

    /// <summary>The baby is asleep: woke up, or still asleep? (Spec 006 §5a).</summary>
    AwakeCheck,
}

/// <summary>A log that waits for the answer to "woke up or still asleep?" (Spec 006 §5a).</summary>
public sealed record AwakeCheck(EntryId Sleep, DateTimeOffset AsleepSince, Func<Task> Log);

/// <summary>A log finished: a toast to show, or an error message.</summary>
public sealed record ActionOutcome(bool Succeeded, string Message);

/// <summary>
/// Every quick-log action of Spec 006 §5 and §6, on top of the existing use cases. Sections call these; after each,
/// <see cref="Done"/> tells Today to refresh and show the toast.
/// </summary>
public sealed class HomeActions(LogEntry log, ReviseEntry revise, TimeProvider time, UndoService? undo = null)
{
    public HomeContext? Context { get; set; }

    public event EventHandler<ActionOutcome>? Done;

    /// <summary>Raised instead of logging when the baby is asleep; Today asks, then calls <see cref="ResolveAwakeCheckAsync"/>.</summary>
    public event EventHandler<AwakeCheck>? AwakeCheckRequested;

    /// <summary>Set by Today: the running sleep of this baby, if any (Spec 006 §5a).</summary>
    public Func<(EntryId Id, DateTimeOffset Since)?> RunningSleep { get; set; } = () => null;

    /// <summary>"Woke up" ends the sleep now and then logs; "still asleep" only logs.</summary>
    public async Task ResolveAwakeCheckAsync(AwakeCheck check, bool wokeUp)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (wokeUp)
            await EndSessionAsync(check.Sleep);
        await check.Log();
    }

    /// <summary>A section asks Today to open a sheet; for the pump yield, which session it is for.</summary>
    public event EventHandler<(HomeSheet Sheet, EntryId? Entry)>? SheetRequested;

    public void RequestSheet(HomeSheet sheet) => SheetRequested?.Invoke(this, (sheet, null));

    public void RequestPumpYield(EntryId id) => SheetRequested?.Invoke(this, (HomeSheet.PumpYield, id));

    public void RequestRecordBirth() => SheetRequested?.Invoke(this, (HomeSheet.RecordBirth, null));

    public Task StartBreastAsync(BreastSide side) => NeedsAwake(() => LogAsync(BreastFeed.Start(side, time.GetLocalNow()), null, EntrySource.Timer, Strings.Get(side == BreastSide.Left ? "Hero.FeedingLeft" : "Hero.FeedingRight")));

    public Task LogBottleAsync(BottleFeed bottle, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(bottle);
        ArgumentNullException.ThrowIfNull(formats);
        return NeedsAwake(() => LogAsync(bottle, EntryTiming.Point(time.GetLocalNow()), EntrySource.Manual, Strings.Format("Entry.Bottle", Formats.MilkName(bottle.Components), formats.Volume(bottle.Total))));
    }

    public Task LogDiaperAsync(DiaperContents contents) =>
        NeedsAwake(() => LogAsync(new DiaperDetails(contents), EntryTiming.Point(time.GetLocalNow()), EntrySource.Manual, Strings.Format("Entry.Diaper", Formats.Contents(contents))));

    public Task StartSleepAsync() =>
        LogAsync(new SleepDetails(), EntryTiming.Session(TimeRange.Open(time.GetLocalNow())), EntrySource.Timer, Strings.Get("Hero.Sleeping"));

    public Task StartPumpAsync() =>
        LogAsync(new PumpDetails(), EntryTiming.Session(TimeRange.Open(time.GetLocalNow())), EntrySource.Timer, Strings.Get("Hero.Pumping"), caregiver: true);

    public Task SwitchSideAsync(EntryId id) =>
        ReviseAsync(id, (e, v, now) => e.Revise<BreastFeed>(d => d.SwitchSide(time.GetLocalNow()), v, now), Strings.Get("Action.SwitchSide"));

    public Task StopBreastAsync(EntryId id) =>
        ReviseAsync(id, (e, v, now) => e.Revise<BreastFeed>(d => d.Complete(time.GetLocalNow()), v, now), Strings.Get("Kind.Feed"));

    /// <summary>
    /// Ends a sleep, or a pumping session with its yield (null when skipped). The yield is a separate revision with its
    /// own stamp from the clock: faking a second stamp inside one revision could later collide with a real one.
    /// </summary>
    public async Task EndSessionAsync(EntryId id, PumpYield? yield = null)
    {
        if (yield is not null)
        {
            var withYield = await revise.ExecuteAsync(id, (e, v, now) => e.Revise<PumpDetails>(d => d.WithYield(yield), v, now), CancellationToken.None);
            if (withYield.IsFailure)
            {
                Done?.Invoke(this, new(false, Strings.Error(withYield.Error?.Code)));
                return;
            }
        }

        await ReviseAsync(id, (e, v, now) =>
        {
            var range = TimeRange.Create(((SessionTiming)e.Timing).Range.Start, time.GetLocalNow());
            return range.IsFailure ? range : e.Retime(EntryTiming.Session(range.Value), v, now);
        }, Strings.Get(yield is null && !IsPump(id) ? "Action.WokeUp" : "Kind.Pump"));
    }

    /// <summary>Set by Today: which running entries are pumping sessions, for the toast text.</summary>
    public Func<EntryId, bool> IsPump { get; set; } = _ => false;

    /// <summary>A diaper, a bottle or a breast feed needs the baby awake: if a sleep runs, ask first (Spec 006 §5a).</summary>
    private Task NeedsAwake(Func<Task> log)
    {
        if (RunningSleep() is not { } sleep)
            return log();
        AwakeCheckRequested?.Invoke(this, new AwakeCheck(sleep.Id, sleep.Since, log));
        return Task.CompletedTask;
    }

    private async Task LogAsync(EntryDetails details, EntryTiming? timing, EntrySource source, string what, bool caregiver = false)
    {
        var context = Context ?? throw new InvalidOperationException("Today has no household or baby yet.");
        var subject = caregiver ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(context.Baby);
        var result = await log.ExecuteAsync(
            new LogEntryCommand(context.Household, subject, details, timing, PersonRef.ThisDevicesPerson, source), CancellationToken.None);
        var message = result.IsSuccess ? Strings.Format("Toast.Logged", what) : Strings.Error(result.Error?.Code);

        // A quick log can be undone for 5 seconds; Undo writes a tombstone (Spec 007 §5).
        if (result.IsSuccess && undo is not null)
            undo.OfferUndoForLog(result.Value.Id, message);
        Done?.Invoke(this, new(result.IsSuccess, undo is not null && result.IsSuccess ? "" : message));
    }

    private async Task ReviseAsync(EntryId id, Func<CareEntry, Coo.Domain.Common.Versioning.HlcStamp, DateTimeOffset, Result> change, string what)
    {
        var result = await revise.ExecuteAsync(id, change, CancellationToken.None);
        Done?.Invoke(this, result.IsSuccess ? new(true, Strings.Format("Toast.Logged", what)) : new(false, Strings.Error(result.Error?.Code)));
    }
}
