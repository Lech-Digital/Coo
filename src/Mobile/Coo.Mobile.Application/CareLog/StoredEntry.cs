using Coo.Domain.CareLog;
using Coo.Domain.Common;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// What a read returns for one stored row (Spec 004 §2). A read never fails because one row is bad: an
/// unreadable row comes back as <see cref="QuarantinedEntry"/>, a row from a newer app as <see cref="ReadOnlyEntry"/>.
/// </summary>
public abstract record StoredEntry
{
    private protected StoredEntry()
    {
    }

    public abstract EntryId Id { get; }

    /// <summary>The kind code as stored. It may be one this build does not know.</summary>
    public abstract string KindCode { get; }

    public abstract DateTimeOffset Start { get; }
}

/// <summary>A normal entry: shown, edited and deleted like any other.</summary>
public sealed record LiveEntry(CareEntry Entry) : StoredEntry
{
    public override EntryId Id => Entry.Id;

    public override string KindCode => Entry.KindCode.Value;

    public override DateTimeOffset Start => Entry.Timing.OccurredAt;
}

/// <summary>Written by a newer app: shown with "update the app to see it", deletable, never editable (ADR 0010 §3).</summary>
public sealed record ReadOnlyEntry(
    EntryId EntryId, string Kind, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, PersonRef? LoggedBy, string Reason) : StoredEntry
{
    public override EntryId Id => EntryId;

    public override string KindCode => Kind;

    public override DateTimeOffset Start => StartedAt;
}

/// <summary>Unreadable: kept byte for byte, listed in "Needs attention", deletable (ADR 0010 §7, G4).</summary>
public sealed record QuarantinedEntry(EntryId EntryId, string Kind, DateTimeOffset StartedAt, string Reason) : StoredEntry
{
    public override EntryId Id => EntryId;

    public override string KindCode => Kind;

    public override DateTimeOffset Start => StartedAt;
}

/// <summary>Counters for the debug screen and, later, the "Needs attention" badge.</summary>
public sealed record StoreCounts(int PendingOutbox, int ReadOnly, int Quarantined);
