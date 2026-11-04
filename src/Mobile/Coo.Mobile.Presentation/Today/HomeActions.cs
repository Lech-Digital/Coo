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
/// <param name="Also">Twins and siblings: "Log for both" is on, and a diaper, bottle or sleep is logged for these babies too.</param>
public sealed record HomeContext(HouseholdId Household, BabyId Baby, IReadOnlyList<BabyId>? Also = null);

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

    /// <summary>
    /// A breastfeed; with "Log for both" on, a tandem feed: the shown baby on <paramref name="side"/>, the next on the other
    /// side (a third on the first side again). Each runs, switches and stops on its own afterwards.
    /// </summary>
    public Task StartBreastAsync(BreastSide side)
    {
        var now = time.GetLocalNow();
        var other = side == BreastSide.Left ? BreastSide.Right : BreastSide.Left;
        return NeedsAwake(() => LogAsync(
            BreastFeed.Start(side, now), null, EntrySource.Timer, Strings.Get(side == BreastSide.Left ? "Hero.FeedingLeft" : "Hero.FeedingRight"),
            forAll: true, perBaby: i => BreastFeed.Start(i % 2 == 0 ? side : other, now)));
    }

    public Task LogBottleAsync(BottleFeed bottle, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(bottle);
        ArgumentNullException.ThrowIfNull(formats);
        return NeedsAwake(() => LogAsync(bottle, EntryTiming.Point(time.GetLocalNow()), EntrySource.Manual, Strings.Format("Entry.Bottle", Formats.MilkName(bottle.Components), formats.Volume(bottle.Total)), forAll: true));
    }

    public Task LogDiaperAsync(DiaperContents contents) =>
        NeedsAwake(() => LogAsync(new DiaperDetails(contents), EntryTiming.Point(time.GetLocalNow()), EntrySource.Manual, Strings.Format("Entry.Diaper", Formats.Contents(contents)), forAll: true));

    public Task StartSleepAsync() =>
        LogAsync(new SleepDetails(), EntryTiming.Session(TimeRange.Open(time.GetLocalNow())), EntrySource.Timer, Strings.Get("Hero.Sleeping"), forAll: true);

    public Task StartPumpAsync() =>
        LogAsync(new PumpDetails(), EntryTiming.Session(TimeRange.Open(time.GetLocalNow())), EntrySource.Timer, Strings.Get("Hero.Pumping"), caregiver: true);

    public Task StartTummyAsync() =>
        LogAsync(new Coo.Domain.CareLog.Kinds.Development.TummyDetails(), EntryTiming.Session(TimeRange.Open(time.GetLocalNow())), EntrySource.Timer, Strings.Get("Hero.Tummy"), forAll: true);

    public Task SwitchSideAsync(EntryId id) =>
        ReviseAsync(id, (e, v, now) => e.Revise<BreastFeed>(d => d.SwitchSide(time.GetLocalNow()), v, now), Strings.Get("Action.SwitchSide") ?? "Switched side");

    public Task PauseBreastAsync(EntryId id) =>
        ReviseAsync(id, (e, v, now) => e.Revise<BreastFeed>(d => d.Pause(time.GetLocalNow()), v, now), Strings.Get("Hero.FeedingPaused") ?? "Feeding paused");

    public Task ResumeBreastAsync(EntryId id, BreastSide side) =>
        ReviseAsync(id, (e, v, now) => e.Revise<BreastFeed>(d => d.Resume(side, time.GetLocalNow()), v, now), Strings.Get(side == BreastSide.Left ? "Hero.FeedingLeft" : "Hero.FeedingRight"));

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

    /// <param name="forAll">Twins and siblings: with "Log for both" on, the same entry for each of <see cref="HomeContext.Also"/>.</param>
    /// <param name="perBaby">The details for the n-th baby (0 is the shown one) when they differ, as a tandem feed's sides do.</param>
    private async Task LogAsync(
        EntryDetails details, EntryTiming? timing, EntrySource source, string what, bool caregiver = false, bool forAll = false,
        Func<int, EntryDetails>? perBaby = null)
    {
        var context = Context ?? throw new InvalidOperationException("Today has no household or baby yet.");
        BabyId[] babies = caregiver || !forAll || context.Also is not { Count: > 0 } also ? [context.Baby] : [context.Baby, .. also];
        var created = new List<EntryId>();
        Result<CareEntry>? failed = null;
        for (var i = 0; i < babies.Length; i++)
        {
            var subject = caregiver ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(babies[i]);
            var result = await log.ExecuteAsync(
                new LogEntryCommand(context.Household, subject, perBaby?.Invoke(i) ?? details, timing, PersonRef.ThisDevicesPerson, source),
                CancellationToken.None);
            if (result.IsSuccess)
                created.Add(result.Value.Id);
            else
                failed ??= result;
        }

        var succeeded = created.Count > 0;
        var message = succeeded ? Strings.Format("Toast.Logged", what) : Strings.Error(failed?.Error?.Code);
        if (succeeded && babies.Length > 1 && BabyNames is { } names)
            message += " · " + string.Join(", ", babies.Select(names));

        // A quick log can be undone for 5 seconds; Undo writes a tombstone for each (Spec 007 §5).
        if (succeeded && undo is not null)
            undo.OfferUndoForLog(created, message);
        Done?.Invoke(this, new(succeeded, undo is not null && succeeded ? "" : message));
    }

    /// <summary>Set by Today: a baby's name, for the toast of a log for several babies.</summary>
    public Func<BabyId, string>? BabyNames { get; set; }

    private async Task ReviseAsync(EntryId id, Func<CareEntry, Coo.Domain.Common.Versioning.HlcStamp, DateTimeOffset, Result> change, string what)
    {
        var result = await revise.ExecuteAsync(id, change, CancellationToken.None);
        Done?.Invoke(this, result.IsSuccess ? new(true, Strings.Format("Toast.Logged", what)) : new(false, Strings.Error(result.Error?.Code)));
    }
}
