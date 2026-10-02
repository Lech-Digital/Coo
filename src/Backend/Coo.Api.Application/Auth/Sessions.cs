using Coo.Domain.Common;

namespace Coo.Api.Application.Auth;

public sealed record RefreshResult(AccessToken AccessToken, string RefreshToken);

/// <summary><c>POST /api/auth/refresh</c> (ADR 0003 §4): rotates on every use; the previous token again means theft.</summary>
public sealed class RefreshSession(IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(180);

    public async Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var hash = AuthSecrets.Hash(refreshToken);
        var found = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        if (found is null || found.Session.IsRevoked)
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var now = time.GetUtcNow();
        if (found.Match == RefreshMatch.Previous || now - found.Session.LastUsedAt > IdleLimit)
        {
            await sessions.RevokeAsync(found.Session.Id, now, cancellationToken);
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        }

        // A concurrent refresh with the same token loses here; its token is now the previous one.
        var next = AuthSecrets.NewToken();
        if (!await sessions.RotateAsync(found.Session.Id, hash, AuthSecrets.Hash(next), now, cancellationToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        return new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), next);
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
