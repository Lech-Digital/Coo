using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.CareLog;

/// <summary>The care log on this phone (Spec 004 §2, §5). Every write leaves its outbox row in the same transaction.</summary>
public interface ICareLogStore
{
    /// <summary>Saves the entry and appends its outbox row, in one transaction, through the codec's write gate.</summary>
    Task SaveAsync(CareEntry entry, CancellationToken cancellationToken);

    Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken);

    /// <summary>The household's live, read-only and quarantined entries starting in [windowStart, windowEnd), newest first.</summary>
    Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>Writes a tombstone for any stored entry, including read-only and quarantined ones, without decoding it.</summary>
    /// <returns>False when there is no such entry, or it is already deleted.</returns>
    Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken);

    Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>Issues version stamps from the persisted hybrid logical clock.</summary>
public interface IVersionClock
{
    Task<HlcStamp> NextAsync(CancellationToken cancellationToken);
}

/// <summary>This install's identity, created once on first launch.</summary>
public interface IDeviceIdentity
{
    Task<DeviceId> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Migrations, data migrations and the integrity scan, run once before the first page (Spec 004 §7).</summary>
public interface IAppStartup
{
    Task<StartupResult> RunAsync(CancellationToken cancellationToken);
}

public enum StartupResult
{
    Ready,

    /// <summary>The database was written by a newer build. Nothing was touched; the app asks for an update (Spec 004 §7.1).</summary>
    DatabaseFromNewerApp,
}

/// <summary>
/// Debug-screen helpers (Spec 004 §8): they write deliberately bad rows with raw SQL, to show the read guard on a
/// real phone. Only the debug screen calls them, and it is not compiled into store builds.
/// </summary>
public interface IStorageDiagnostics
{
    string DatabasePath { get; }

    Task InsertMalformedRowAsync(Coo.Domain.Common.HouseholdId household, Coo.Domain.Common.BabyId baby, CancellationToken cancellationToken);

    Task InsertForeignKindRowAsync(Coo.Domain.Common.HouseholdId household, Coo.Domain.Common.BabyId baby, CancellationToken cancellationToken);
}

/// <summary>Application-level error codes, alongside the Domain's.</summary>
public static class ApplicationErrors
{
    public const string EntryNotFound = "entry.not-found";
    public const string EntryNotEditable = "entry.not-editable";
}

/// <summary>The "keep both" decisions (Spec 007 §6). Each one syncs, so no phone asks about the pair again.</summary>
public interface IDuplicateDismissalStore
{
    Task SaveAsync(Coo.Domain.CareLog.Duplicates.DuplicateDismissal dismissal, CancellationToken cancellationToken);

    Task<IReadOnlySet<Coo.Domain.CareLog.Duplicates.EntryPair>> ListAsync(HouseholdId household, CancellationToken cancellationToken);
}
