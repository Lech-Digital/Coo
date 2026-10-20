using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.CareLog;

/// <summary>A change the server did not accept (ADR 0002 §5): the entry stays, with the reason's code.</summary>
public sealed record RejectedEntry(StoredEntry Entry, string Reason);

/// <summary>
/// Entries that are probably one event or one feed (Spec 011d §4): every duplicate pair and, for feeds, every pair close
/// together, joined while they share an entry. <see cref="OpenPairs"/> are the pairs no one kept both of; a group is
/// shown only while it has one. <see cref="Merge"/> is set for feed groups: the merge, or why there is none.
/// </summary>
public sealed record AttentionGroup(IReadOnlyList<CareEntry> Entries, IReadOnlyList<EntryPair> OpenPairs, FeedMergeOutcome? Merge)
{
    /// <summary>Breast feeds or bottle feeds, which can be merged (Spec 011d §3).</summary>
    public bool IsFeeds => Merge is not null;

    public bool CanMerge => Merge?.IsMergeable == true;

    /// <summary>Why a feed group cannot be merged; null when it can, or when it is not feeds.</summary>
    public FeedMergeRefusal? MergeRefusal => Merge?.Refusal;
}

/// <summary>What needs a person (Spec 007 §6, Spec 011 §2): groups of duplicates, unreadable entries, entries from a newer app, rejected changes.</summary>
public sealed record AttentionSummary(
    IReadOnlyList<AttentionGroup> Groups,
    IReadOnlyList<QuarantinedEntry> Quarantined,
    IReadOnlyList<ReadOnlyEntry> ReadOnly,
    IReadOnlyList<RejectedEntry> Rejected)
{
    /// <summary>Every open pair of every group, for a pair-by-pair view ("Keep both" names one pair).</summary>
    public IReadOnlyList<DuplicatePair> Duplicates =>
        [.. Groups.SelectMany(g => g.OpenPairs.Select(p => new DuplicatePair(p, g.Entries.Single(e => e.Id == p.First), g.Entries.Single(e => e.Id == p.Second))))];

    public int Count => Groups.Count + Quarantined.Count + ReadOnly.Count + Rejected.Count;
}

/// <summary>
/// Finds what needs attention. The duplicate check runs over the last week of this household's entries, with the
/// "keep both" decisions applied (ADR 0002 §9). Rejected care entries are listed whatever their date.
/// </summary>
public sealed class GetAttention(ICareLogStore store, IDuplicateDismissalStore dismissals, ISyncStore sync)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public async Task<AttentionSummary> ExecuteAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await store.ListAsync(household, now - Window, now.AddDays(1), cancellationToken);
        var live = stored.OfType<LiveEntry>().Select(l => l.Entry).ToList();
        var dismissed = await dismissals.ListAsync(household, cancellationToken);
        var rejections = (await sync.ListRejectionsAsync(household, cancellationToken))
            .Where(r => r.EntityType == SyncEntityTypes.CareEntry).ToList();
        var rejected = new List<RejectedEntry>();
        var activeRejectedIds = new HashSet<EntryId>();
        foreach (var rejection in rejections)
        {
            // An unreadable or newer-version entry already sits in its own group, with the action that fits it.
            if (await store.FindAsync(new EntryId(rejection.EntityId), cancellationToken) is LiveEntry { Entry.IsDeleted: false } entry)
            {
                rejected.Add(new RejectedEntry(entry, rejection.Reason));
                activeRejectedIds.Add(entry.Id);
            }
        }

        var groups = AttentionGroups.Find(live, dismissed, e => activeRejectedIds.Contains(e.Id), now);
        return new AttentionSummary(groups, [.. stored.OfType<QuarantinedEntry>()], [.. stored.OfType<ReadOnlyEntry>()], rejected);
    }
}

/// <summary>
/// Builds the groups (Spec 011d §4): the pairs are every duplicate pair (Spec 001 §9.1) and every two feeds close
/// together (<see cref="FeedMerge.AreCloseTogether"/>, same baby and variant), and a group is every entry those pairs
/// connect, so a feed close only to a longer one still joins its group. Dismissed pairs still connect a group; they
/// are just not open. A group with no open pair is not shown.
/// </summary>
internal static class AttentionGroups
{
    private static readonly IReadOnlySet<EntryPair> NoneDismissed = new HashSet<EntryPair>();

    public static IReadOnlyList<AttentionGroup> Find(
        IReadOnlyList<CareEntry> live, IReadOnlySet<EntryPair> dismissed, Func<CareEntry, bool> isReadOnly, DateTimeOffset now)
    {
        var pairs = DuplicateDetector.Find(live, live, NoneDismissed, now).Select(p => p.Pair).ToHashSet();
        // A feed still running is not "close" to the one before it yet: the parent is busy feeding (duplicates still count).
        var feeds = live.Where(e => !e.IsDeleted && e.Details is FeedingDetails && !e.Timing.IsInProgress).ToList();
        for (var i = 0; i < feeds.Count; i++)
        {
            for (var j = i + 1; j < feeds.Count; j++)
            {
                if (FeedMerge.AreCloseTogether(feeds[i], feeds[j]))
                    pairs.Add(new EntryPair(feeds[i].Id, feeds[j].Id));
            }
        }

        var root = new Dictionary<EntryId, EntryId>();
        foreach (var pair in pairs)
            root[Find(root, pair.First)] = Find(root, pair.Second);

        var byId = live.ToDictionary(e => e.Id);
        var groups = new List<AttentionGroup>();
        foreach (var component in root.Keys.ToList().GroupBy(id => Find(root, id)))
        {
            var entries = component.Select(id => byId[id]).OrderBy(e => e.Timing.OccurredAt).ThenBy(e => e.Id.Value).ToList();
            var position = entries.Select((e, i) => (e.Id, i)).ToDictionary(x => x.Id, x => x.i);
            var open = pairs
                .Where(p => position.ContainsKey(p.First) && !dismissed.Contains(p))
                .OrderBy(p => Math.Min(position[p.First], position[p.Second]))
                .ThenBy(p => Math.Max(position[p.First], position[p.Second]))
                .ToList();
            if (open.Count == 0)
                continue;

            var merge = entries.All(e => e.Details is FeedingDetails) ? FeedMerge.Plan(entries, isReadOnly) : null;
            groups.Add(new AttentionGroup(entries, open, merge));
        }

        return [.. groups.OrderByDescending(g => g.Entries.Max(e => e.Timing.OccurredAt))];
    }

    /// <summary>Union-find's find, with path halving.</summary>
    private static EntryId Find(Dictionary<EntryId, EntryId> root, EntryId id)
    {
        while (root.TryGetValue(id, out var parent) && parent != id)
        {
            var grandparent = root.TryGetValue(parent, out var g) ? g : parent;
            root[id] = grandparent;
            id = grandparent;
        }

        root.TryAdd(id, id);
        return id;
    }
}

/// <summary>"Keep both": a synced dismissal, so no phone asks about the pair again (Spec 007 §6).</summary>
public sealed class DismissDuplicate(IDuplicateDismissalStore dismissals, IVersionClock clock, TimeProvider time)
{
    public async Task ExecuteAsync(HouseholdId household, EntryPair pair, CancellationToken cancellationToken)
    {
        var dismissal = DuplicateDismissal.Create(
            new DismissalId(Guid.CreateVersion7(time.GetUtcNow())), household, pair, await clock.NextAsync(cancellationToken));
        await dismissals.SaveAsync(dismissal, cancellationToken);
    }

    /// <summary>"Keep all": each open pair of a group gets its own dismissal (Spec 011d §4).</summary>
    public async Task ExecuteAsync(HouseholdId household, IReadOnlyCollection<EntryPair> pairs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        foreach (var pair in pairs)
            await ExecuteAsync(household, pair, cancellationToken);
    }
}
