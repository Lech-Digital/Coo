using Coo.Contracts.Sync;
using Coo.Contracts.Wearables;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Development;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Wearables;

/// <summary>The phone's side of the Wear OS Data Layer (ADR 0009 §8), implemented on Android in Coo.Mobile.Platform.</summary>
public interface IWearableBridge
{
    /// <summary>Publishes what the watch shows; the platform skips it when the content has not changed.</summary>
    Task PublishAsync(WatchSnapshotV1 snapshot, CancellationToken cancellationToken);
}

/// <summary>What happened to a state from the watch; the platform acknowledges (deletes the item) in every case.</summary>
public enum WatchApplyOutcome
{
    Applied,

    /// <summary>The phone already holds this version or a newer one: a duplicate, or a create overtaken by its delete.</summary>
    Superseded,

    /// <summary>Not a valid entry, an unknown baby or schema: nothing written (ADR 0002 §11).</summary>
    Rejected,
}

public sealed record WatchApplyResult(WatchApplyOutcome Outcome, string? Reason = null);

/// <summary>
/// The snapshot the watch shows (ADR 0009 §3): the babies with the one in focus, what runs now, the last feed and side,
/// the last diaper, awake since, the next nap window, the last bottle's amount, units and language.
/// </summary>
public static class WatchSnapshots
{
    /// <param name="babies">The family's live babies, the one in focus on the phone first.</param>
    public static WatchSnapshotV1 Build(
        HouseholdSettings household, IReadOnlyList<BabyProfile> babies, TodaySummary? today, SleepPrediction? next, string language, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(household);
        ArgumentNullException.ThrowIfNull(babies);
        var running = (today?.Running ?? []).Select(e => new WatchRunningV1(
            e.Id.Value,
            e.Subject is BabySubject b ? b.BabyId.Value : null,
            e.Details switch
            {
                BreastFeed => "breast",
                SleepDetails => "sleep",
                PumpDetails => "pump",
                TummyDetails => "tummy",
                _ => e.KindCode.Value,
            },
            e.Timing.OccurredAt.ToUnixTimeMilliseconds(),
            e.Details is BreastFeed feed ? Side(feed.LastSide) : null)).ToList();
        var lastBottle = today?.TodayEntries.OfType<LiveEntry>().Select(l => l.Entry.Details).OfType<BottleFeed>().FirstOrDefault();
        return new WatchSnapshotV1(
            WatchSchema.Current,
            [.. babies.Select(b => new WatchBabyV1(b.Id.Value, b.Name))],
            babies.Count > 0 ? babies[0].Id.Value : null,
            running,
            today?.LastFeed?.Timing.OccurredAt.ToUnixTimeMilliseconds(),
            today?.LastFeed?.Details is BreastFeed last ? Side(last.LastSide) : null,
            today?.LastDiaper?.Timing.OccurredAt.ToUnixTimeMilliseconds(),
            today?.AwakeSince?.ToUnixTimeMilliseconds(),
            next is { IsPersonal: true } ? new WatchNapWindowV1(next.Earliest.ToUnixTimeMilliseconds(), next.Latest.ToUnixTimeMilliseconds(), next.IsBedtime) : null,
            lastBottle?.Total.Millilitres,
            household.VolumeUnit == VolumeUnit.Ounces ? "oz" : "ml",
            language,
            now.ToUnixTimeMilliseconds());
    }

    private static string Side(BreastSide side) => side == BreastSide.Left ? "left" : "right";
}

/// <summary>
/// A state from the watch, written through the phone's ordinary write path with the watch's ID and version (ADR 0009 §2):
/// a new entry, a phone timer stopped on the watch, or a delete. Arrival order does not matter: an older version is
/// superseded, so a create that arrives after its delete never brings the entry back.
/// </summary>
public sealed class ApplyWatchState(ICareLogStore store, IHouseholdStore households, TimeProvider time)
{
    public async Task<WatchApplyResult> ExecuteAsync(WatchEntryStateV1 state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion is < WatchSchema.Oldest or > WatchSchema.Current)
            return new(WatchApplyOutcome.Rejected, "watch.schema");
        var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
        var baby = new BabyId(state.BabyId);
        if (household is null || !(await households.ListBabiesAsync(household.Id, cancellationToken)).Any(b => b.Id == baby && !b.IsDeleted))
            return new(WatchApplyOutcome.Rejected, "watch.baby");

        var id = new EntryId(state.EntryId);
        var version = new HlcStamp(state.Version.WallClockMs, state.Version.Counter, new DeviceId(state.Version.DeviceId));
        var stored = await store.FindVersionAsync(id, cancellationToken);
        if (stored is { } known && (known.Version >= version || known.IsDeleted))
            return new(WatchApplyOutcome.Superseded);

        if (state.Deleted)
        {
            if (stored is null)
                return new(WatchApplyOutcome.Superseded);
            await store.DeleteAsync(id, version, cancellationToken);
            return new(WatchApplyOutcome.Applied);
        }

        var start = DateTimeOffset.FromUnixTimeMilliseconds(state.StartMs);
        DateTimeOffset? end = state.EndMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var now = time.GetUtcNow();

        // A timer that runs on the phone, stopped on the watch: only its end comes from the watch.
        if (stored is not null)
            return await StopAsync(id, end, version, now, cancellationToken);

        var made = Details(state, start, end);
        if (made.IsFailure)
            return new(WatchApplyOutcome.Rejected, made.Error!.Code);
        var (details, timing) = made.Value;
        var entry = CareEntry.Create(id, household.Id, EntrySubject.Baby(baby), details, timing, PersonRef.ThisDevicesPerson, EntrySource.Watch, version, now);
        if (entry.IsFailure)
            return new(WatchApplyOutcome.Rejected, entry.Error!.Code);
        await store.SaveAsync(entry.Value, cancellationToken);
        return new(WatchApplyOutcome.Applied);
    }

    private async Task<WatchApplyResult> StopAsync(EntryId id, DateTimeOffset? end, HlcStamp version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (end is not { } at || await store.FindAsync(id, cancellationToken) is not LiveEntry { Entry: var entry } || !entry.Timing.IsInProgress)
            return new(WatchApplyOutcome.Superseded);
        var stopped = entry.Details switch
        {
            BreastFeed => entry.Revise<BreastFeed>(d => d.Complete(at), version, now),
            _ when entry.Timing is SessionTiming session && TimeRange.Create(session.Range.Start, at) is { IsSuccess: true } range =>
                entry.Retime(EntryTiming.Session(range.Value), version, now),
            _ => Result.Failure(DomainError.Of("watch.stop")),
        };
        if (stopped.IsFailure)
            return new(WatchApplyOutcome.Rejected, stopped.Error!.Code);
        await store.SaveAsync(entry, cancellationToken);
        return new(WatchApplyOutcome.Applied);
    }

    private static Result<(EntryDetails Details, EntryTiming? Timing)> Details(WatchEntryStateV1 state, DateTimeOffset start, DateTimeOffset? end)
    {
        switch (state.Kind)
        {
            case "diaper":
                DiaperContents? contents = state.Contents switch { "wet" => DiaperContents.Wet, "dirty" => DiaperContents.Dirty, "mixed" => DiaperContents.Mixed, _ => null };
                return contents is { } c ? (new DiaperDetails(c), EntryTiming.Point(start)) : Fail("watch.contents");
            case "bottle":
                Milk? milk = state.Milk switch { "breast" => Milk.ExpressedBreastMilk, "formula" => Milk.Formula, _ => null };
                if (milk is null || state.BottleMl is not { } ml || Volume.FromMillilitres(ml) is not { IsSuccess: true } volume
                    || BottleFeed.Of(milk.Value, volume.Value) is not { IsSuccess: true } bottle)
                    return Fail("watch.bottle");
                return (bottle.Value, EntryTiming.Point(start));
            case "breast":
                BreastSide? side = state.Side switch { "left" => BreastSide.Left, "right" => BreastSide.Right, _ => null };
                if (side is null)
                    return Fail("watch.side");
                var feed = BreastFeed.Start(side.Value, start);
                if (end is { } stop)
                {
                    var completed = feed.Complete(stop);
                    if (completed.IsFailure)
                        return Result<(EntryDetails, EntryTiming?)>.Failure(completed.Error!);
                    feed = completed.Value;
                }

                return (feed, null);
            case "sleep":
                var range = TimeRange.Create(start, end);
                return range.IsSuccess ? (new SleepDetails(), EntryTiming.Session(range.Value)) : Result<(EntryDetails, EntryTiming?)>.Failure(range.Error!);
            default:
                return Fail("watch.kind");
        }

        static Result<(EntryDetails, EntryTiming?)> Fail(string code) => Result<(EntryDetails, EntryTiming?)>.Failure(DomainError.Of(code));
    }
}
