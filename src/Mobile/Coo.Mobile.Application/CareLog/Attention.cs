using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.CareLog;

/// <summary>A change the server did not accept (ADR 0002 §5): the entry stays, with the reason's code.</summary>
public sealed record RejectedEntry(StoredEntry Entry, string Reason);

/// <summary>What needs a person (Spec 007 §6, Spec 011 §2): duplicates, unreadable entries, entries from a newer app, rejected changes.</summary>
public sealed record AttentionSummary(
    IReadOnlyList<DuplicatePair> Duplicates,
    IReadOnlyList<QuarantinedEntry> Quarantined,
    IReadOnlyList<ReadOnlyEntry> ReadOnly,
    IReadOnlyList<RejectedEntry> Rejected)
{
    public int Count => Duplicates.Count + Quarantined.Count + ReadOnly.Count + Rejected.Count;
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
        var pairs = DuplicateDetector.Find(live, live, dismissed, now);

        // Rejected care entries, whatever their date; deleted ones are done with. Other types are counted on the debug
        // screen only (ruling 14).
        var rejected = new List<RejectedEntry>();
        foreach (var rejection in await sync.ListRejectionsAsync(household, cancellationToken))
        {
            if (rejection.EntityType != SyncEntityTypes.CareEntry)
                continue;
            if (await store.FindAsync(new EntryId(rejection.EntityId), cancellationToken) is { } entry && entry is not LiveEntry { Entry.IsDeleted: true })
                rejected.Add(new RejectedEntry(entry, rejection.Reason));
        }

        return new AttentionSummary(pairs, [.. stored.OfType<QuarantinedEntry>()], [.. stored.OfType<ReadOnlyEntry>()], rejected);
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
}
