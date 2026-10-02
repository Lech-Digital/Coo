using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// One refresh at a time (Spec 010 §3.3). Callers that saw the same expired token join the refresh in flight and share
/// its outcome, a failure included, instead of starting their own: ten requests refused while the server answers 429
/// make one attempt, not ten. A network failure, 429 or 5xx propagates as <see cref="AuthUnavailableException"/> and
/// keeps the session; a refused refresh signs out as expired and keeps every local record.
/// </summary>
internal sealed class TokenRefresher(ISessionStore sessions, IAuthApi api, Account account, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

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
            var session = await sessions.GetAsync(cancellationToken);
            if (session is null)
                return null;
            if (session.AccessToken != staleAccessToken)
                return session.AccessToken;

            SessionTokens tokens;
            try
            {
                tokens = await api.RefreshAsync(session.RefreshToken, cancellationToken);
            }
            catch (AuthRejectedException)
            {
                await account.SignedOutAsync(sessionExpired: true, cancellationToken);
                return null;
            }

            await sessions.SaveAsync(
                session with { AccessToken = tokens.AccessToken, AccessTokenExpiresAt = tokens.AccessTokenExpiresAt, RefreshToken = tokens.RefreshToken },
                cancellationToken);
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
