using Coo.Contracts.Sync;

namespace Coo.Contracts.Auth;

// Wire DTOs for sign-in and sessions (Spec 010 §3, §4.1). Versioned by adding, never by renaming (ADR 0001 §1).

public sealed record NonceResponse(string Nonce);

public sealed record GoogleSignInRequest(string IdToken, string DeviceName, Guid DeviceId, Guid ProvisionalHouseholdId);

public sealed record UserDto(Guid Id, string Email, string DisplayName);

/// <summary><see cref="Outcome"/> is one of <see cref="AuthOutcomes"/>.</summary>
public sealed record SessionResponse(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, UserDto User, Guid HouseholdId, string Outcome);

/// <summary><see cref="NextRefreshToken"/> is the token the phone proposes as the next one (Spec 011 §8.2); 0.10.0 phones omit it.</summary>
public sealed record RefreshRequest(string RefreshToken, string? NextRefreshToken = null);

public sealed record TokensResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

/// <summary><see cref="Role"/> is one of <see cref="AuthRoles"/>. <see cref="JoinedAt"/> is set in <c>memberships</c> (Spec 011b).</summary>
public sealed record MembershipDto(Guid HouseholdId, string Role, DateTimeOffset? JoinedAt = null);

public sealed record DeviceSessionDto(Guid Id, string DeviceName, DateTimeOffset LastUsedAt, bool IsCurrent);

public sealed record MeResponse(
    UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices, ConsentDto? Consent = null,
    IReadOnlyList<MembershipDto>? Memberships = null);

public sealed record DeleteAccountRequest(bool Confirm);

/// <summary>Every refusal: a code, never server text (ADR 0006).</summary>
public sealed record ErrorResponse(string Code);

public static class AuthOutcomes
{
    public const string Adopted = "adopted";
    public const string Created = "created";
    public const string Existing = "existing";
    public const string Joined = "joined";
}

public static class AuthRoles
{
    public const string Owner = "owner";
    public const string Parent = "parent";
    public const string Caregiver = "caregiver";

    /// <summary>Follows the log and logs nothing (a grandparent). Phones before 1.6 do not know it.</summary>
    public const string Viewer = "viewer";
}
