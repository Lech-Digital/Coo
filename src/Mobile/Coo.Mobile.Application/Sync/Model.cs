using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.Sync;

// A record's state as it travels (ADR 0002 §3). Rows, not Domain objects: a pulled entry of a kind this build does not know
// must be stored as received (ADR 0010 §3), so nothing here is validated. Details stay a JSON string.

public abstract record RecordPayload
{
    private protected RecordPayload()
    {
    }
}

public sealed record CareEntryPayload(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, string DetailsJson) : RecordPayload;

public sealed record BabyProfilePayload(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate) : RecordPayload;

public sealed record HouseholdSettingsPayload(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds) : RecordPayload;

public sealed record DismissalPayload(Guid EntryA, Guid EntryB) : RecordPayload;

/// <summary>One record's change: its current state under its newest outbox row's change ID (Spec 011 §6.1).</summary>
/// <param name="LastOutboxSeq">The newest outbox row this change answers for; a final result deletes the record's rows up to it.</param>
public sealed record OutgoingChange(
    Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, long LastOutboxSeq, RecordPayload? Payload);

public enum PushOutcome
{
    Applied,
    Superseded,
    Rejected,
}

public sealed record PushResult(Guid ChangeId, PushOutcome Outcome, string? Reason);

public sealed record PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, RecordPayload Payload);

/// <param name="Since">
/// Where the pull started (the cursor and head it sent): a page whose household lost its position on the phone meanwhile is
/// not applied (final review C1). Null for a page of unknown origin, which is applied as before.
/// </param>
public sealed record PulledPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead, SyncCursor? Since = null);

/// <summary>Where a household's pull stands; <see cref="BootstrapHead"/> is sent with every page of a bootstrap.</summary>
public sealed record SyncCursor(long Cursor, long? BootstrapHead);

/// <summary>For the debug screen (Spec 011 §11).</summary>
public sealed record SyncCounts(long Cursor, int Outbox, int Rejections);

public sealed record SyncRejection(string EntityType, Guid EntityId, string Reason, DateTimeOffset RejectedAt);

public enum MembershipStatus
{
    Active,
    Revoked,

    /// <summary>Anything that is not a clear answer: keep the soft-lock and ask again later (ADR 0002 §10).</summary>
    Unknown,
}

public enum ConsentState
{
    Unknown,
    Given,
    Declined,
}

public sealed record LocalBaby(BabyId Id, string Name);

public sealed record LocalSummary(int EntryCount, IReadOnlyList<LocalBaby> Babies);

/// <summary>Bring my entries over (Spec 011 §4): each local baby to a household baby, or null for "Add as a new baby".</summary>
public sealed record BringOverPlan(HouseholdId From, HouseholdId To, IReadOnlyDictionary<BabyId, BabyId?> Babies);

public enum CycleOutcome
{
    Synced,
    Failed,
    NotSignedIn,
    ConsentNeeded,
    ConsentDeclined,
    JoinPending,
    SoftLocked,
    Removed,
    ClientTooOld,
    Interrupted,
}

/// <param name="DataChanged">Local data changed (pulled records, a move): pages refresh and the duplicate check reruns.</param>
/// <param name="Error">The failure's type or refusal, for the debug screen; never shown to the person.</param>
/// <param name="Household">For <see cref="CycleOutcome.Removed"/>: the household the person was removed from.</param>
/// <param name="RunAgain">Another cycle should run at once (a lifted soft-lock), not at the next tick.</param>
/// <param name="RefreshFamilies">The family list should be refreshed: a soft-lock, a not-a-member, a family created (Spec 011b §7.1).</param>
/// <param name="NotAMember">Families that answered <c>sync.not-a-member</c> this cycle (spec §9).</param>
public sealed record CycleReport(
    CycleOutcome Outcome, bool DataChanged = false, string? Error = null, HouseholdId? Household = null, bool RunAgain = false,
    bool RefreshFamilies = false, IReadOnlyList<HouseholdId>? NotAMember = null);
