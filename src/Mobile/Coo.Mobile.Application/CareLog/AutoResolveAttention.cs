using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// Result of an auto-resolution pass.
/// </summary>
public sealed record AutoResolveResult(int MergedFeedGroups, int ResolvedDuplicates, int ClearedRejections);

/// <summary>
/// Automatically resolves attention items (merges consecutive feeds, removes exact duplicate entries, and cleans stale rejections).
/// </summary>
public sealed class AutoResolveAttention(
    GetAttention getAttention,
    MergeFeeds merge,
    ICareLogStore store,
    ISyncStore sync,
    IVersionClock clock)
{
    public async Task<AutoResolveResult> ExecuteAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attention = await getAttention.ExecuteAsync(household, now, cancellationToken);
        var mergedFeeds = 0;
        var resolvedDuplicates = 0;
        var clearedRejections = 0;

        // 1. Auto-merge feeding groups that qualify (e.g. Left 5m, 3m pause, Right 10m)
        foreach (var group in attention.Groups.Where(g => g.IsFeeds))
        {
            if (FeedAutoMerge.CanAutoMerge(group))
            {
                var result = await merge.ExecuteAsync([.. group.Entries.Select(e => e.Id)], cancellationToken);
                if (result.IsMerged)
                    mergedFeeds++;
            }
        }

        // 2. Auto-resolve exact duplicate pairs (same kind, same details within 2 mins)
        var toDelete = new List<EntryId>();
        foreach (var group in attention.Groups.Where(g => !g.IsFeeds))
        {
            var entries = group.Entries.OrderBy(e => e.Timing.OccurredAt).ThenBy(e => e.Id.Value).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                if (toDelete.Contains(entries[i].Id))
                    continue;

                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (toDelete.Contains(entries[j].Id))
                        continue;

                    if (FeedAutoMerge.CanAutoResolveDuplicate(entries[i], entries[j]))
                    {
                        toDelete.Add(entries[j].Id);
                        resolvedDuplicates++;
                    }
                }
            }
        }

        if (toDelete.Count > 0)
        {
            var deletes = new List<EntryVersion>(toDelete.Count);
            foreach (var id in toDelete)
                deletes.Add(new EntryVersion(id, await clock.NextAsync(cancellationToken)));

            await store.WriteAllAsync(new CareLogWrites([], deletes, []), cancellationToken);
        }

        // 3. Clear stale rejections for entries that are deleted or resolved
        var rejections = (await sync.ListRejectionsAsync(household, cancellationToken))
            .Where(r => r.EntityType == SyncEntityTypes.CareEntry).ToList();
        foreach (var rejection in rejections)
        {
            var id = new EntryId(rejection.EntityId);
            if (await store.FindAsync(id, cancellationToken) is not LiveEntry { Entry.IsDeleted: false })
            {
                await sync.DeleteRejectionAsync(household, rejection.EntityType, rejection.EntityId, cancellationToken);
                clearedRejections++;
            }
        }

        return new AutoResolveResult(mergedFeeds, resolvedDuplicates, clearedRejections);
    }
}
