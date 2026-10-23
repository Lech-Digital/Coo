using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.CareLog;

/// <summary>A one-tap log from outside the app: a home-screen widget, or a running timer's notification.</summary>
public enum QuickAction
{
    FeedLeft,
    FeedRight,
    SwitchSide,
    PauseFeed,
    StopFeed,
    Wet,
    Dirty,
    Mixed,
    Sleep,
    WokeUp,
    Pump,
    StopPump,
}

/// <param name="Done">What was written, for the confirmation; null when nothing was (see <paramref name="Error"/>).</param>
/// <param name="Error">An error code: <see cref="QuickActions.NoBaby"/>, <see cref="QuickActions.NothingRunning"/>, or a Domain one.</param>
public sealed record QuickActionResult(QuickAction? Done, string? Error, BreastSide? Side = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The quick logs that need no screen (Spec 006 §5–6 without Today): for the first baby of the shown family, through
/// the ordinary write path, so they sync, plan reminders and can be edited like any entry. Starting a feed ends a running
/// sleep first (she woke for it); a diaper does not (night changes happen asleep). Starting what already runs, or stopping
/// what does not, changes nothing and says so.
/// </summary>
public sealed class QuickActions(IHouseholdStore households, GetToday getToday, LogEntry log, ReviseEntry revise, TimeProvider time)
{
    public const string NoBaby = "quick.no-baby";
    public const string NothingRunning = "quick.nothing-running";
    public const string AlreadyRunning = "quick.already-running";

    public async Task<QuickActionResult> ExecuteAsync(QuickAction action, CancellationToken cancellationToken)
    {
        var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null
            : (await households.ListBabiesAsync(household.Id, cancellationToken)).FirstOrDefault(b => !b.IsDeleted && b.IsBorn);
        if (household is null || baby is null)
            return new(null, NoBaby);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var now = time.GetUtcNow();
        var today = await getToday.ExecuteAsync(household.Id, baby.Id, zone, now, cancellationToken);
        CareEntry? Running<T>() where T : EntryDetails => today.Running.FirstOrDefault(e => e.Details is T);

        async Task<QuickActionResult> Log(EntryDetails details, EntryTiming? timing, bool caregiver = false)
        {
            var subject = caregiver ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(baby.Id);
            var result = await log.ExecuteAsync(
                new LogEntryCommand(household.Id, subject, details, timing, PersonRef.ThisDevicesPerson, timing is SessionTiming or null ? EntrySource.Timer : EntrySource.Manual),
                cancellationToken);
            return result.IsSuccess ? new(action, null) : new(null, result.Error!.Code);
        }

        async Task<QuickActionResult> Revise(CareEntry? entry, Func<CareEntry, Coo.Domain.Common.Versioning.HlcStamp, DateTimeOffset, Result> change, BreastSide? side = null)
        {
            if (entry is null)
                return new(null, NothingRunning);
            var result = await revise.ExecuteAsync(entry.Id, change, cancellationToken);
            return result.IsSuccess ? new(action, null, side) : new(null, result.Error!.Code);
        }

        async Task<QuickActionResult> End(CareEntry? session) => await Revise(session, (e, v, at) =>
        {
            var range = TimeRange.Create(((SessionTiming)e.Timing).Range.Start, now);
            return range.IsFailure ? range : e.Retime(EntryTiming.Session(range.Value), v, at);
        });

        switch (action)
        {
            case QuickAction.FeedLeft or QuickAction.FeedRight:
                var side = action == QuickAction.FeedLeft ? BreastSide.Left : BreastSide.Right;
                if (Running<BreastFeed>() is { Details: BreastFeed running } feed)
                {
                    // Paused: resumes on that side. Running on the other side: switches. Running on it: nothing to do.
                    if (running.Segments[^1].End is not null)
                        return await Revise(feed, (e, v, at) => e.Revise<BreastFeed>(d => d.Resume(side, now), v, at), side);
                    return running.LastSide == side
                        ? new(null, AlreadyRunning)
                        : await Revise(feed, (e, v, at) => e.Revise<BreastFeed>(d => d.SwitchSide(now), v, at), side);
                }

                if (Running<SleepDetails>() is { } asleep)
                    await End(asleep);
                var started = await Log(BreastFeed.Start(side, now), null);
                return started with { Side = side };
            case QuickAction.SwitchSide:
                return await Revise(Running<BreastFeed>(), (e, v, at) => e.Revise<BreastFeed>(d => d.SwitchSide(now), v, at));
            case QuickAction.PauseFeed:
                return await Revise(Running<BreastFeed>(), (e, v, at) => e.Revise<BreastFeed>(d => d.Pause(now), v, at));
            case QuickAction.StopFeed:
                return await Revise(Running<BreastFeed>(), (e, v, at) => e.Revise<BreastFeed>(d => d.Complete(now), v, at));
            case QuickAction.Wet or QuickAction.Dirty or QuickAction.Mixed:
                var contents = action switch { QuickAction.Wet => DiaperContents.Wet, QuickAction.Dirty => DiaperContents.Dirty, _ => DiaperContents.Mixed };
                return await Log(new DiaperDetails(contents), EntryTiming.Point(now));
            case QuickAction.Sleep:
                return Running<SleepDetails>() is not null
                    ? new(null, AlreadyRunning)
                    : await Log(new SleepDetails(), EntryTiming.Session(TimeRange.Open(now)));
            case QuickAction.WokeUp:
                return await End(Running<SleepDetails>());
            case QuickAction.Pump:
                return Running<PumpDetails>() is not null
                    ? new(null, AlreadyRunning)
                    : await Log(new PumpDetails(), EntryTiming.Session(TimeRange.Open(now)), caregiver: true);
            case QuickAction.StopPump:
                return await End(Running<PumpDetails>());
            default:
                return new(null, NothingRunning);
        }
    }
}
