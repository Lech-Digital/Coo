namespace Coo.Contracts.Households;

// Wire DTOs for members and invitations (Spec 011b §10.1). Versioned by adding, never by renaming (ADR 0001 §1).
// Roles are AuthRoles strings; outcomes are the constants below.

public sealed record CreateInvitationRequest(string Role);

/// <summary><see cref="Code"/> is the plain code, unhyphenated; it exists only in this answer (spec §3).</summary>
public sealed record InvitationCreatedResponse(Guid InvitationId, string Code, string Link, DateTimeOffset ExpiresAt);

public sealed record MemberDto(Guid UserId, string Name, string Email, string Role, DateTimeOffset JoinedAt, bool IsYou);

public sealed record InvitationDto(Guid InvitationId, string Role, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary><see cref="Invitations"/> is empty for a Caregiver.</summary>
public sealed record MembersResponse(IReadOnlyList<MemberDto> Members, IReadOnlyList<InvitationDto> Invitations);

public sealed record LeaveRequest(bool Confirm);

/// <summary><see cref="Outcome"/> is one of <see cref="LeaveOutcomes"/>.</summary>
public sealed record LeaveResponse(string Outcome);

public sealed record CreateHouseholdRequest(Guid HouseholdId);

/// <summary>The code travels in a body, never a path or query (spec §4.3): it stays out of every log.</summary>
public sealed record InvitationCodeRequest(string Code);

public sealed record InvitationPreviewResponse(
    Guid HouseholdId, string InviterName, IReadOnlyList<string> BabyNames, string Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

/// <summary><see cref="Outcome"/> is one of <see cref="AcceptOutcomes"/>.</summary>
public sealed record AcceptInvitationResponse(Guid HouseholdId, string Role, string Outcome);

/// <summary>Spec 011b §10.1. Once released, a code is never renamed (ADR 0006).</summary>
public static class HouseholdErrors
{
    public const string InvitationInvalid = "invitation.invalid";
    public const string InvitationRoleUnsupported = "invitation.role-unsupported";
    public const string InvitationTooMany = "invitation.too-many";
    public const string Forbidden = "household.forbidden";
    public const string MemberNotFound = "household.member-not-found";
    public const string OwnerCannotLeave = "household.owner-cannot-leave";
    public const string LastFamily = "household.last-family";
    public const string ConfirmationRequired = "household.confirmation-required";
    public const string IdTaken = "household.id-taken";
    public const string AlreadyHasFamily = "household.already-has-family";

    /// <summary>An unreadable body (ruling 13). The app never sends one.</summary>
    public const string Malformed = "household.malformed";

    /// <summary>Reused from sign-in (Spec 010 §4.1): the limiter's answer to preview, accept and creating invitations.</summary>
    public const string RateLimited = "auth.rate-limited";
}

public static class LeaveOutcomes
{
    public const string Left = "left";
    public const string Deleted = "deleted";
}

public static class AcceptOutcomes
{
    public const string Joined = "joined";
    public const string Rejoined = "rejoined";
    public const string AlreadyMember = "already-member";
}

/// <summary>Invitation links live on the API's host (spec §4.1, R1): moving them needs an app update.</summary>
public static class InvitationLinks
{
    public const string Host = "api.coo.lechdigital.nl";
    public const string PathPrefix = "/i/";
    public const string Base = "https://" + Host + PathPrefix;

    public static string For(string code) => Base + code;
}
