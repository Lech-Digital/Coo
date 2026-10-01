using Coo.Domain.CareLog;
using Coo.Domain.Common;

namespace Coo.Api.Application.CareLog;

/// <summary>
/// The server's care log (Spec 008 §4). It takes domain objects only, writes through the codecs' write gate, and
/// gives every accepted write the household's next <c>seq</c> (ADR 0002 §6) in the same transaction.
/// </summary>
public interface ICareEntryStore
{
    /// <summary>Inserts or replaces the entry, and returns the <c>seq</c> it was given.</summary>
    Task<long> SaveAsync(CareEntry entry, CancellationToken cancellationToken);

    Task<ServerStoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken);

    /// <summary>Every row, tombstones included, through the read guard. For the integrity scan (G5).</summary>
    IAsyncEnumerable<ServerStoredEntry> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>The households the server knows. Sign-in (Increment 10) adds members and roles.</summary>
public interface IHouseholdRegistry
{
    /// <summary>Creates the household's row and its <c>seq</c> counter. Idempotent.</summary>
    Task EnsureAsync(HouseholdId id, CancellationToken cancellationToken);
}

/// <summary>A stored row, as the read guard (G4) sees it.</summary>
public abstract record ServerStoredEntry(EntryId Id);

public sealed record LiveServerEntry(CareEntry Entry, long Seq) : ServerStoredEntry(Entry.Id);

/// <summary>A row that does not decode. It is kept, logged and counted, never dropped (ADR 0010 G4).</summary>
public sealed record UnreadableServerEntry(EntryId Id, string Kind, string Reason) : ServerStoredEntry(Id);

/// <summary>The household does not exist on the server: a write for it is refused.</summary>
public sealed class HouseholdNotFoundException : Exception
{
    public HouseholdNotFoundException()
    {
    }

    public HouseholdNotFoundException(string message)
        : base(message)
    {
    }

    public HouseholdNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
