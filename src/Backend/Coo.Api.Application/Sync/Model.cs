using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

// What a push carries once the endpoint has read the JSON: details stay a string here (no System.Text.Json in this layer).

public sealed record IncomingChange(Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, ChangePayload? Payload);

public abstract record ChangePayload
{
    private protected ChangePayload()
    {
    }
}

public sealed record CareEntryPayload(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, string DetailsJson) : ChangePayload;

/// <param name="Sex">"girl" or "boy"; null when not chosen, or from a phone from before 1.9.0.</param>
public sealed record BabyProfilePayload(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate, string? Sex = null) : ChangePayload;

public sealed record HouseholdSettingsPayload(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds) : ChangePayload;

public sealed record DismissalPayload(Guid EntryA, Guid EntryB) : ChangePayload;

/// <summary>A change that passed validation: the Domain object to store, or a tombstone.</summary>
public abstract record SyncWrite(string EntityType, Guid EntityId, HlcStamp Version);

/// <summary><see cref="ReceivedDetails"/> is the document the phone sent; its unknown properties are kept (ruling 6).</summary>
public sealed record CareEntryWrite(CareEntry Entry, string ReceivedDetails) : SyncWrite(SyncEntityTypes.CareEntry, Entry.Id.Value, Entry.Version);

public sealed record BabyProfileWrite(BabyProfile Baby) : SyncWrite(SyncEntityTypes.BabyProfile, Baby.Id.Value, Baby.Version);

public sealed record HouseholdSettingsWrite(HouseholdSettings Settings) : SyncWrite(SyncEntityTypes.HouseholdSettings, Settings.Id.Value, Settings.Version);

public sealed record DismissalWrite(DuplicateDismissal Dismissal) : SyncWrite(SyncEntityTypes.DuplicateDismissal, Dismissal.Id.Value, Dismissal.Version);

/// <summary>A delete: only the version travels (ruling 1).</summary>
public sealed record TombstoneWrite : SyncWrite
{
    public TombstoneWrite(string entityType, Guid entityId, HlcStamp version)
        : base(entityType, entityId, version)
    {
    }
}

public enum ChangeOutcome
{
    Applied,
    Superseded,
    Rejected,
}

public sealed record ChangeResult(ChangeOutcome Outcome, string? Reason = null)
{
    public static ChangeResult Applied { get; } = new(ChangeOutcome.Applied);

    public static ChangeResult Superseded { get; } = new(ChangeOutcome.Superseded);

    public static ChangeResult Rejected(string reason) => new(ChangeOutcome.Rejected, reason);
}

public sealed record ChangeVerdict(Guid ChangeId, ChangeResult Result);

/// <summary>What the store holds for a record, read under its row lock.</summary>
public sealed record StoredVersion(HouseholdId Household, HlcStamp Version);

public enum WriteDecision
{
    Apply,
    Superseded,
    HouseholdMismatch,
}

public sealed record PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, long Seq, ChangePayload Payload);

public sealed record PullPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead);
