using Coo.Domain.Common;

namespace Coo.Mobile.Application.Auth;

public sealed record AccountProfile(UserId UserId, string Name, string Email);

/// <summary>
/// What the phone keeps after signing in (Spec 010 §3.3): both tokens, who is signed in, the account's household, and
/// whether this phone's entries are still to join it at the first sync (Spec 010 §3.2, Q1).
/// </summary>
public sealed record StoredSession(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AccountProfile Profile, HouseholdId AccountHousehold,
    bool JoinPending);

/// <summary>The session on this phone. Unreadable means none: the person signs in again; it never crashes.</summary>
public interface ISessionStore
{
    Task<StoredSession?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(StoredSession session, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Encrypted key-value storage backed by the Android Keystore (MAUI <c>SecureStorage</c> in the host).</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Android's Google account sheet (Credential Manager, Spec 010a): a Google ID token carrying the nonce.</summary>
public interface ISignInProvider
{
    /// <summary>Null when the person closed the sheet. Throws <see cref="SignInUnavailableException"/> when it failed.</summary>
    Task<string?> GetIdTokenAsync(string nonce, CancellationToken cancellationToken);
}

/// <summary>The phone's model, for example "Pixel 10", shown in the account's device list.</summary>
public interface IDeviceName
{
    string Name { get; }
}

/// <summary>Which case of Spec 010 §3.2 the server reported.</summary>
public enum SignInOutcome
{
    Adopted,
    Created,
    Existing,
    Joined,
}

public sealed record SessionTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

public sealed record GoogleSignIn(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId);

public sealed record SignInGrant(SessionTokens Tokens, AccountProfile Profile, HouseholdId Household, SignInOutcome Outcome);

/// <summary><c>/api/auth</c> calls that need no access token.</summary>
public interface IAuthApi
{
    Task<string> GetNonceAsync(CancellationToken cancellationToken);

    Task<SignInGrant> SignInWithGoogleAsync(GoogleSignIn request, CancellationToken cancellationToken);

    Task<SessionTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

public sealed record DeviceSessionInfo(string Name, DateTimeOffset LastUsedAt, bool IsThisDevice);

public sealed record AccountOverview(AccountProfile Profile, HouseholdId? Household, string? Role, IReadOnlyList<DeviceSessionInfo> Devices);

/// <summary>Calls made with the access token, refreshed as needed by the infrastructure.</summary>
public interface IAccountApi
{
    Task SignOutAsync(CancellationToken cancellationToken);

    Task<AccountOverview> GetMeAsync(CancellationToken cancellationToken);

    Task DeleteAccountAsync(CancellationToken cancellationToken);
}

/// <summary>The server could not be asked: no network, a timeout, <c>429</c> or <c>5xx</c>. "Try later", never "signed out".</summary>
public sealed class AuthUnavailableException : Exception
{
    public AuthUnavailableException()
    {
    }

    public AuthUnavailableException(string message)
        : base(message)
    {
    }

    public AuthUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server answered and refused; <see cref="Code"/> is its error code (Spec 010 §4.1).</summary>
public sealed class AuthRejectedException : Exception
{
    public AuthRejectedException()
        : this("unknown")
    {
    }

    public AuthRejectedException(string message)
        : base(message)
    {
    }

    public AuthRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code => Message;
}

/// <summary>Android's account sheet failed (no Google account, Play services unavailable, …).</summary>
public sealed class SignInUnavailableException : Exception
{
    public SignInUnavailableException()
    {
    }

    public SignInUnavailableException(string message)
        : base(message)
    {
    }

    public SignInUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
