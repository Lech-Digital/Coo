using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Mobile.Application.CareLog;

/// <summary>What needs a person (Spec 007 §6): probable duplicates, unreadable entries, entries from a newer app.</summary>
public sealed record AttentionSummary(
    IReadOnlyList<DuplicatePair> Duplicates,
    IReadOnlyList<QuarantinedEntry> Quarantined,
    IReadOnlyList<ReadOnlyEntry> ReadOnly)
{
    public int Count => Duplicates.Count + Quarantined.Count + ReadOnly.Count;
}

/// <summary>
/// Finds what needs attention. The duplicate check runs over the last week of this household's entries, with the
/// "keep both" decisions applied (ADR 0002 §9). Groups are listed in order; sync rejections are added with sync.
/// </summary>
public sealed class GetAttention(ICareLogStore store, IDuplicateDismissalStore dismissals)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public async Task<AttentionSummary> ExecuteAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await store.ListAsync(household, now - Window, now.AddDays(1), cancellationToken);
        var live = stored.OfType<LiveEntry>().Select(l => l.Entry).ToList();
        var dismissed = await dismissals.ListAsync(household, cancellationToken);
        var pairs = DuplicateDetector.Find(live, live, dismissed, now);
        return new AttentionSummary(pairs, [.. stored.OfType<QuarantinedEntry>()], [.. stored.OfType<ReadOnlyEntry>()]);
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
