using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

/// <summary>One device session (Spec 010 §4.2): one per phone per user.</summary>
public readonly record struct DeviceSessionId
{
    public DeviceSessionId(Guid value) =>
        Value = value == Guid.Empty ? throw new ArgumentException("A session ID cannot be Guid.Empty.", nameof(value)) : value;

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>What a verified Google ID token says about the person (ADR 0003 §3: only these).</summary>
public sealed record ExternalIdentity(string Subject, string Email, string DisplayName, string Nonce);

/// <summary>
/// Verifies a Google ID token (ADR 0003 §5): signature, <c>iss</c>, <c>aud</c>, expiry, <c>email_verified</c>. Returns
/// null on any failure, including Google's keys being unreachable. The nonce is checked by the use case.
/// </summary>
public interface IExternalIdentityVerifier
{
    Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary><c>auth_nonces</c>: single-use sign-in nonces, by hash.</summary>
public interface IAuthNonceStore
{
    Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken);

    /// <summary>Marks the nonce used, only if it is unused and was issued at or after <paramref name="issuedNotBefore"/>. True when this call consumed it.</summary>
    Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken);

    Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed record DeviceSession(
    DeviceSessionId Id, UserId UserId, DeviceId DeviceId, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>Whether a refresh token is the session's current one, or the one it replaced (presenting that is theft).</summary>
public enum RefreshMatch
{
    Current,
    Previous,
}

public sealed record RefreshLookup(DeviceSession Session, RefreshMatch Match);

/// <summary><c>device_sessions</c> (ADR 0003 §4): refresh tokens are stored only as hashes.</summary>
public interface IDeviceSessionStore
{
    /// <summary>Stores a new session. An earlier session of the same user on the same device is replaced.</summary>
    Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken);

    Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically, only if the session is not revoked and its current hash is <paramref name="expectedHash"/>: the current
    /// hash becomes the previous one, <paramref name="newHash"/> the current one, and the session was last used now.
    /// </summary>
    Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken);

    Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>For every authenticated request: a revoked or replaced session is refused at once.</summary>
    Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken);

    /// <summary>Every session of the user, revoked ones included.</summary>
    Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Whether this device has had a session for this user, revoked or not.</summary>
    Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken);
}

public sealed record UserAccount(UserId Id, string Email, string DisplayName);

public sealed record NewAccount(UserId UserId, string GoogleSubject, string Email, string DisplayName, HouseholdId Household, DateTimeOffset CreatedAt);

public sealed record OwnershipTransfer(HouseholdId Household, UserId NewOwner);

public sealed record AccountDeletionPlan(IReadOnlyList<HouseholdId> HouseholdsToDelete, IReadOnlyList<OwnershipTransfer> OwnershipTransfers);

/// <summary>Users (ASP.NET Core Identity, ADR 0003 §3), households and memberships.</summary>
public interface IAccountStore
{
    Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken);

    Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken);

    Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken);

    /// <summary>In one transaction: the user, their Google login, the household (created by them), and their Owner membership.</summary>
    Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken);

    /// <summary>A household and its Owner membership, for a user who has none.</summary>
    Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken);

    Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: the ownership transfers, the households to delete with all of their care data, then the user
    /// with their logins, device sessions and memberships (Spec 010 §5).
    /// </summary>
    Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Issues the 15-minute access token (ADR 0003 §4), signed with the active key.</summary>
public interface ITokenIssuer
{
    AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now);
}
