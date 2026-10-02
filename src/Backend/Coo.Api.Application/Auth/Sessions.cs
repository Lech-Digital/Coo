using Coo.Domain.Common;

namespace Coo.Api.Application.Auth;

public sealed record RefreshResult(AccessToken AccessToken, string RefreshToken);

/// <summary>
/// <c>POST /api/auth/refresh</c> (ADR 0003 §4, Spec 011 §8.2). Rotates on every use; the previous token again means theft,
/// unless it comes with the next token the phone proposed and that token is now the current one: then it is the phone
/// retrying after a lost response, and it gets a fresh access token while nothing changes.
/// </summary>
public sealed class RefreshSession(IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(180);

    /// <summary>A proposal is 256 random bits in base64url (43 characters); anything shorter is not one.</summary>
    public const int NextTokenMinLength = 43;
    public const int NextTokenMaxLength = 128;

    /// <summary>A 0.10.0 phone: no proposal, the server picks the next token.</summary>
    public Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, CancellationToken cancellationToken) =>
        ExecuteAsync(refreshToken, null, cancellationToken);

    public async Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, string? nextRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        if (nextRefreshToken is not null
            && (nextRefreshToken.Length is < NextTokenMinLength or > NextTokenMaxLength || nextRefreshToken == refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var hash = AuthSecrets.Hash(refreshToken);
        var nextHash = nextRefreshToken is null ? null : AuthSecrets.Hash(nextRefreshToken);
        var found = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        if (found is null || found.Session.IsRevoked)
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var now = time.GetUtcNow();
        if (now - found.Session.LastUsedAt > IdleLimit)
            return await RevokeAsync(found.Session, now, cancellationToken);

        if (found.Match == RefreshMatch.Previous)
        {
            return IsRetry(found, nextHash)
                ? new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), nextRefreshToken!)
                : await RevokeAsync(found.Session, now, cancellationToken);
        }

        var next = nextRefreshToken ?? AuthSecrets.NewToken();
        if (await sessions.RotateAsync(found.Session.Id, hash, AuthSecrets.Hash(next), now, cancellationToken))
            return new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), next);

        // Lost the compare-and-swap: a concurrent refresh with the same token won. With the same proposal that was this
        // phone's own retry; anything else gets what a concurrent loser always got.
        var again = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        return again is { Match: RefreshMatch.Previous } && !again.Session.IsRevoked && IsRetry(again, nextHash)
            ? new RefreshResult(tokens.Issue(again.Session.UserId, again.Session.Id, now), nextRefreshToken!)
            : Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
    }

    private static bool IsRetry(RefreshLookup found, string? nextHash) => nextHash is not null && found.CurrentHash == nextHash;

    private async Task<Result<RefreshResult>> RevokeAsync(DeviceSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await sessions.RevokeAsync(session.Id, now, cancellationToken);
        return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
    }
}

/// <summary><c>POST /api/auth/sign-out</c>: revokes this device session. Nothing on the phone is deleted (ADR 0003 §4).</summary>
public sealed class SignOutSession(IDeviceSessionStore sessions, TimeProvider time)
{
    public Task ExecuteAsync(DeviceSessionId session, CancellationToken cancellationToken) =>
        sessions.RevokeAsync(session, time.GetUtcNow(), cancellationToken);
}

/// <summary>The daily clean-up (Spec 010 §4.2): nonce rows older than a day go.</summary>
public sealed class PurgeAuthNonces(IAuthNonceStore nonces, TimeProvider time)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public Task<int> ExecuteAsync(CancellationToken cancellationToken) =>
        nonces.DeleteIssuedBeforeAsync(time.GetUtcNow() - KeepFor, cancellationToken);
}
