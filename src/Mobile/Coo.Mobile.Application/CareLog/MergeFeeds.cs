using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// What a merge changed, so <see cref="UndoFeedMerge"/> can put it back (Spec 011d §3, "Undo"). <see cref="MergedVersion"/>
/// is the merged feed's version right after the merge: a newer one means someone changed it since.
/// </summary>
public sealed record FeedMergeReceipt(
    EntryId SurvivorId, EntryDetails PreviousDetails, Note? PreviousNote, IReadOnlyList<EntryId> DeletedIds, HlcStamp MergedVersion);

/// <summary>A merge that happened (<see cref="Merged"/>), or the reason it did not (<see cref="Refusal"/>). Exactly one is set.</summary>
public sealed record MergeFeedsResult(FeedMergeReceipt? Merged, FeedMergeRefusal? Refusal)
{
    public bool IsMerged => Merged is not null;

    /// <summary>The feed that holds the merge: the earliest one.</summary>
    public EntryId? MergedId => Merged?.SurvivorId;
}

/// <summary>
/// Merges feeds that are close together into one (Spec 011d §3): the earliest is revised to hold every part and the
/// others are deleted, in one transaction with their outbox rows, so a merge never half-applies.
/// </summary>
public sealed class MergeFeeds(ICareLogStore store, ISyncStore sync, IVersionClock clock, TimeProvider time)
{
    /// <exception cref="ArgumentException">Fewer than two feeds, or the same feed twice.</exception>
    public async Task<MergeFeedsResult> ExecuteAsync(IReadOnlyCollection<EntryId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count < 2 || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Merging needs two or more different feeds.", nameof(ids));

        var feeds = new List<CareEntry>(ids.Count);
        foreach (var id in ids)
        {
            // Missing, read-only or quarantined: this phone cannot change it (ADR 0010 §3).
            if (await store.FindAsync(id, cancellationToken) is not LiveEntry { Entry: var entry })
                return Refused(FeedMergeRefusal.ReadOnly);
            feeds.Add(entry);
        }

        var rejected = await Rejections.CareEntryIdsAsync(sync, feeds[0].HouseholdId, cancellationToken);
        var outcome = FeedMerge.Plan(feeds, f => rejected.Contains(f.Id));
        if (outcome.Plan is not { } plan)
            return Refused(outcome.Refusal!.Value);

        var survivor = feeds.Single(f => f.Id == plan.SurvivorId);
        var (previousDetails, previousNote) = (survivor.Details, survivor.Note);

        var revised = survivor.Revise<FeedingDetails>(
            _ => Result<FeedingDetails>.Ok(plan.Details), await clock.NextAsync(cancellationToken), time.GetUtcNow());
        if (revised.IsFailure)
            throw new InvalidOperationException($"A planned merge failed the entry's rules: {revised.Error!.Code}.");
        if (survivor.Note != plan.Note)
            survivor.SetNote(plan.Note, await clock.NextAsync(cancellationToken));

        var deletes = new List<EntryVersion>(plan.DeletedIds.Count);
        foreach (var id in plan.DeletedIds)
            deletes.Add(new EntryVersion(id, await clock.NextAsync(cancellationToken)));

        // A feed deleted meanwhile (the other phone's tombstone) fails the whole write, and nothing changes.
        return await store.WriteAllAsync(new CareLogWrites([survivor], deletes, []), cancellationToken)
            ? new MergeFeedsResult(new FeedMergeReceipt(survivor.Id, previousDetails, previousNote, plan.DeletedIds, survivor.Version), null)
            : Refused(FeedMergeRefusal.ReadOnly);
    }

    private static MergeFeedsResult Refused(FeedMergeRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Undoes a merge (Spec 011d §3): the earliest feed gets its previous details and note back and the deleted feeds come
/// back, each with a new version, in one transaction. It syncs as ordinary changes. What changed meanwhile is left
/// alone: a merged feed deleted or changed since is not revised, and a feed already back is not restored again.
/// </summary>
public sealed class UndoFeedMerge(ICareLogStore store, IVersionClock clock, TimeProvider time)
{
    /// <returns>False when nothing was written.</returns>
    public async Task<bool> ExecuteAsync(FeedMergeReceipt merged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(merged);

        var saves = new List<CareEntry>(1);
        // A merged feed changed since the merge (the other phone's edit, pulled meanwhile) keeps that change.
        if (await store.FindAsync(merged.SurvivorId, cancellationToken) is LiveEntry { Entry: { IsDeleted: false } survivor }
            && survivor.Version == merged.MergedVersion)
        {
            var changed = false;
            if (!survivor.Details.Equals(merged.PreviousDetails))
            {
                changed |= survivor.Revise<EntryDetails>(
                    _ => Result<EntryDetails>.Ok(merged.PreviousDetails), await clock.NextAsync(cancellationToken), time.GetUtcNow()).IsSuccess;
            }

            if (survivor.Note != merged.PreviousNote)
                changed |= survivor.SetNote(merged.PreviousNote, await clock.NextAsync(cancellationToken)).IsSuccess;
            if (changed)
                saves.Add(survivor);
        }

        var restores = new List<EntryVersion>(merged.DeletedIds.Count);
        foreach (var id in merged.DeletedIds)
        {
            if (await store.FindAsync(id, cancellationToken) is LiveEntry { Entry.IsDeleted: true })
                restores.Add(new EntryVersion(id, await clock.NextAsync(cancellationToken)));
        }

        return (saves.Count > 0 || restores.Count > 0)
            && await store.WriteAllAsync(new CareLogWrites(saves, [], restores), cancellationToken);
    }
}

/// <summary>The care entries the server did not accept (ADR 0002 §5): this phone does not change them on its own.</summary>
internal static class Rejections
{
    public static async Task<IReadOnlySet<EntryId>> CareEntryIdsAsync(ISyncStore sync, HouseholdId household, CancellationToken cancellationToken) =>
        (await sync.ListRejectionsAsync(household, cancellationToken))
            .Where(r => r.EntityType == SyncEntityTypes.CareEntry)
            .Select(r => new EntryId(r.EntityId))
            .ToHashSet();
}
