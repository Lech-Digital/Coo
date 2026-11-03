using System.Text.Json;

namespace Coo.Contracts.Sync;

// Wire DTOs for sync (Spec 011 §6–§7, ADR 0002 §3, §5, §6, §10, §11). Versioned by adding, never by renaming.
// JsonElement keeps a care entry's details exactly as written; the Application layers see them as a string.

public sealed record HlcStampDto(long WallClockMs, int Counter, Guid DeviceId);

public sealed record CareEntryDto(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, JsonElement Details);

/// <param name="Sex">"girl" or "boy", for the growth chart (1.9.0); null when not chosen. Older phones and servers omit it.</param>
public sealed record BabyProfileDto(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate, string? Sex = null);

/// <summary><see cref="VolumeUnit"/> is <c>ml</c> or <c>oz</c>; <see cref="HiddenKinds"/> are kind codes.</summary>
public sealed record HouseholdSettingsDto(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds);

public sealed record DuplicateDismissalDto(Guid EntryA, Guid EntryB);

/// <summary>One change (ADR 0002 §3). <see cref="Operation"/> is one of <see cref="SyncOperations"/>; an upsert carries the
/// payload of its <see cref="EntityType"/>, a delete carries none.</summary>
public sealed record SyncChangeDto(
    Guid ChangeId, string EntityType, Guid EntityId, string Operation, HlcStampDto Version,
    CareEntryDto? CareEntry = null, BabyProfileDto? BabyProfile = null, HouseholdSettingsDto? HouseholdSettings = null,
    DuplicateDismissalDto? DuplicateDismissal = null);

public sealed record PushRequest(Guid HouseholdId, Guid DeviceId, int SchemaVersion, IReadOnlyList<SyncChangeDto> Changes);

/// <summary><see cref="Result"/> is one of <see cref="ChangeResults"/>; <see cref="Reason"/> is set for <c>rejected</c>.</summary>
public sealed record ChangeResultDto(Guid ChangeId, string Result, string? Reason);

public sealed record PushResponse(IReadOnlyList<ChangeResultDto> Results);

/// <summary>A record's current state, tombstones included, with its full payload.</summary>
public sealed record SyncRecordDto(
    string EntityType, Guid EntityId, HlcStampDto Version, bool IsDeleted, long Seq,
    CareEntryDto? CareEntry = null, BabyProfileDto? BabyProfile = null, HouseholdSettingsDto? HouseholdSettings = null,
    DuplicateDismissalDto? DuplicateDismissal = null);

public sealed record PullResponse(IReadOnlyList<SyncRecordDto> Records, long Cursor, bool HasMore, long? BootstrapHead);

/// <summary><see cref="Status"/> is one of <see cref="MembershipStatuses"/>.</summary>
public sealed record MembershipStatusResponse(string Status);

public sealed record ConsentRequest(string Purpose, string Wording, string? AppVersion = null, string? Language = null);

public sealed record ConsentDto(string Wording, DateTimeOffset GivenAt);

/// <summary>The fields of a problem body the phone reads (ADR 0002 §10): only this exact type, for this household, counts.</summary>
public sealed record ProblemResponse(string? Type, int? Status, Guid? HouseholdId);

public static class SyncEntityTypes
{
    public const string CareEntry = "CareEntry";
    public const string BabyProfile = "BabyProfile";
    public const string HouseholdSettings = "HouseholdSettings";
    public const string DuplicateDismissal = "DuplicateDismissal";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CareEntry, BabyProfile, HouseholdSettings, DuplicateDismissal,
    };
}

public static class SyncOperations
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

public static class ChangeResults
{
    public const string Applied = "applied";
    public const string Superseded = "superseded";
    public const string Rejected = "rejected";
}

/// <summary>Request-level refusals (Spec 011 §7.1). Once released, a code is never renamed.</summary>
public static class SyncErrors
{
    public const string Malformed = "sync.malformed";
    public const string BatchTooLarge = "sync.batch-too-large";
    public const string ConsentRequired = "sync.consent-required";
    public const string NotAMember = "sync.not-a-member";
    public const string ClientTooOld = "client-too-old";
    public const string ConsentUnknownWording = "consent.unknown-wording";
    public const string MembershipRevokedType = "https://coo.lechdigital.nl/problems/membership-revoked";
}

/// <summary>Per-change <c>rejected</c> reasons beside the Domain's own codes (Spec 011 §7.1; ruling 2).</summary>
public static class RejectionReasons
{
    public const string SchemaUnsupported = "sync.schema-unsupported";
    public const string StampInFuture = "sync.stamp-in-future";
    public const string HouseholdMismatch = "sync.household-mismatch";
    public const string PersonUnresolved = "sync.person-unresolved";
    public const string RoleForbidden = "sync.role-forbidden";
    public const string ChangeMalformed = "sync.change-malformed";
}

public static class SchemaVersions
{
    public const int Current = 1;

    /// <summary>N−1 (ADR 0002 §11). There is no version before 1, so today the oldest accepted is the current one.</summary>
    public const int Oldest = 1;
}

public static class SyncLimits
{
    public const int MaxPushChanges = 200;
    public const int MaxPullRecords = 500;
}

/// <summary>The consent of Spec 011 §3. A new wording asks again.</summary>
public static class CareDataConsent
{
    public const string Purpose = "care-data";
    public const string CurrentWording = "care-data-v1";
}

public static class MembershipStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
