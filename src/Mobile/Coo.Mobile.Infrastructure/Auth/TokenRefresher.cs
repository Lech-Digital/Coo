using System.Buffers.Text;
using System.Security.Cryptography;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// One refresh at a time (Spec 010 §3.3). Callers that saw the same expired token join the refresh in flight and share
/// its outcome, a failure included, instead of starting their own: ten requests refused while the server answers 429
/// make one attempt, not ten. A network failure, 408, 429, 5xx or any refusal other than <c>auth.refresh-invalid</c>
/// (a captive portal's 403, a proxy's 404) propagates as <see cref="AuthUnavailableException"/> and keeps the session;
/// only <c>auth.refresh-invalid</c> signs out as expired, keeping every local record. A refresh started before a sign-out
/// or sign-in writes back only if the account's generation has not moved (Spec 011 §8.1); it proposes the next refresh
/// token and keeps proposing the same one until an answer arrives (§8.2).
/// </summary>
internal sealed class TokenRefresher(ISessionStore sessions, IAuthApi api, Account account, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    /// <summary>The only refusal /api/auth/refresh sends (Spec 010 §4.1); any other is not the server ending the session.</summary>
    private const string RefreshInvalid = "auth.refresh-invalid";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _flightLock = new();
    private Task<string?>? _flight;
    private string? _flightFor;

    /// <summary>The token to send, refreshed first when it expires within a minute. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(cancellationToken);
        if (session is null)
            return null;
        return session.AccessTokenExpiresAt - time.GetUtcNow() > Margin
            ? session.AccessToken
            : await RefreshAsync(session.AccessToken, cancellationToken);
    }

    /// <summary>A new access token in place of <paramref name="staleAccessToken"/>; null when the session is gone.</summary>
    /// <remarks>
    /// Cancelling only stops this caller's wait. The refresh itself is shared, so it runs on the refresher's lifetime
    /// (bounded by the HttpClient timeout): one caller giving up must not fail the others.
    /// </remarks>
    public Task<string?> RefreshAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staleAccessToken);
        Task<string?> flight;
        TaskCompletionSource<string?>? started = null;
        lock (_flightLock)
        {
            if (_flight is { IsCompleted: false } && _flightFor == staleAccessToken)
            {
                flight = _flight;
            }
            else
            {
                started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _flight = flight = started.Task;
                _flightFor = staleAccessToken;
            }
        }

        // Started outside the lock: signing out raises Account.Changed, whose handlers are not this class's to hold a lock over.
        if (started is not null)
            _ = RunAsync(staleAccessToken, started);
        return flight.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private async Task RunAsync(string staleAccessToken, TaskCompletionSource<string?> flight)
    {
        try
        {
            flight.SetResult(await RefreshCoreAsync(staleAccessToken, _lifetime.Token));
        }
        catch (OperationCanceledException e)
        {
            flight.SetCanceled(e.CancellationToken);
        }
#pragma warning disable CA1031 // Every failure goes to the callers sharing this refresh; none may be lost.
        catch (Exception e)
#pragma warning restore CA1031
        {
            flight.SetException(e);
        }
    }

    private async Task<string?> RefreshCoreAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var generation = account.Generation;
            var session = await sessions.GetAsync(cancellationToken);
            if (session is null)
                return null;
            if (session.AccessToken != staleAccessToken)
                return session.AccessToken;

            // The proposal is stored before it is sent, so a lost response is retried with the same pair. An existing proposal
            // is sent without re-checking the generation: sending writes nothing here, and the write-back below is guarded.
            var next = session.PendingRefreshToken;
            if (next is null)
            {
                next = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
                if (!await account.TrySetPendingRefreshTokenAsync(generation, next, cancellationToken))
                    return null;
            }

            SessionTokens tokens;
            try
            {
                tokens = await api.RefreshAsync(session.RefreshToken, next, cancellationToken);
            }
            catch (AuthRejectedException e) when (e.Code == RefreshInvalid)
            {
                await account.TryExpireAsync(generation, cancellationToken);
                return null;
            }
            catch (AuthRejectedException e)
            {
                // A captive portal's 403, a proxy's 404 during a deploy: not the server refusing this session.
                throw new AuthUnavailableException("The refresh was refused with " + e.Code + ".", e);
            }

            return await account.TryReplaceTokensAsync(generation, tokens, cancellationToken) ? tokens.AccessToken : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
