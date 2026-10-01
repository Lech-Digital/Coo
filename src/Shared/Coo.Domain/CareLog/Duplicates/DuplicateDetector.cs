using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.CareLog.Duplicates;

/// <summary>Two entry IDs in canonical order, so (a, b) and (b, a) are the same pair (Spec 001 §9.2).</summary>
public readonly record struct EntryPair
{
    public EntryPair(EntryId a, EntryId b)
    {
        if (a == b)
            throw new ArgumentException("An entry cannot pair with itself.", nameof(b));

        (First, Second) = GuidOrder.Compare(a.Value, b.Value) < 0 ? (a, b) : (b, a);
    }

    public EntryId First { get; }

    public EntryId Second { get; }
}

/// <summary>Two entries that are probably the same event, for a person to resolve.</summary>
public sealed record DuplicatePair(EntryPair Pair, CareEntry First, CareEntry Second);

/// <summary>
/// Finds probable duplicates (Spec 001 §9.1). It holds no kind-specific logic: each kind's
/// <see cref="DuplicateRule"/> decides. Duplicates are flagged, never merged.
/// </summary>
public static class DuplicateDetector
{
    public static IReadOnlyList<DuplicatePair> Find(
        IReadOnlyCollection<CareEntry> candidates,
        IReadOnlyCollection<CareEntry> existing,
        IReadOnlySet<EntryPair> dismissed,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(dismissed);

        var live = candidates.Where(e => !e.IsDeleted).ToList();
        var others = live.Concat(existing.Where(e => !e.IsDeleted)).DistinctBy(e => e.Id).ToList();

        var found = new Dictionary<EntryPair, DuplicatePair>();
        foreach (var candidate in live)
        {
            foreach (var other in others)
            {
                if (other.Id == candidate.Id || other.Kind != candidate.Kind || other.Subject != candidate.Subject)
                    continue;

                var pair = new EntryPair(candidate.Id, other.Id);
                if (found.ContainsKey(pair) || dismissed.Contains(pair))
                    continue;

                if (candidate.Kind.Duplicates is { } rule && rule.Matches(candidate, other, now))
                {
                    var (first, second) = pair.First == candidate.Id ? (candidate, other) : (other, candidate);
                    found[pair] = new DuplicatePair(pair, first, second);
                }
            }
        }

        return [.. found.Values];
    }
}

/// <summary>A synced "keep both" decision, so no caregiver is asked about the same pair again (Spec 001 §9.2).</summary>
public sealed class DuplicateDismissal : SyncedRecord<DismissalId>
{
    private DuplicateDismissal(DismissalId id, HouseholdId householdId, EntryPair pair, HlcStamp version, bool isDeleted)
        : base(id, householdId, version, isDeleted) => Pair = pair;

    public EntryPair Pair { get; }

    public static DuplicateDismissal Create(DismissalId id, HouseholdId householdId, EntryPair pair, HlcStamp version) =>
        new(id, householdId, pair, version, isDeleted: false);

    internal static DuplicateDismissal Rehydrate(DismissalId id, HouseholdId householdId, EntryPair pair, HlcStamp version, bool isDeleted) =>
        new(id, householdId, pair, version, isDeleted);
}
