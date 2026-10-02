using Coo.Domain.Common;

namespace Coo.Mobile.Application.Auth;

/// <summary>The Account section's states (Spec 010 §6).</summary>
public abstract record AccountState;

/// <param name="SessionExpired">The server refused the session (revoked, reused, idle): "Sign in again to keep sharing."</param>
public sealed record SignedOut(bool SessionExpired) : AccountState;

public sealed record SignedIn(AccountProfile Profile) : AccountState;

/// <summary>The same account on another phone (Spec 010 §3.2, Q1): this phone's entries join at the first sync.</summary>
public sealed record JoinPending(AccountProfile Profile, HouseholdId AccountHousehold) : AccountState;

/// <summary>
/// Who is signed in on this phone, in step with the session store. One per app. Being signed out never blocks logging
/// (ADR 0003 §4), and nothing here touches local care data. Every sign-in and sign-out moves <see cref="Generation"/>
/// under one lock (Spec 011 §8.1); work started before a move (a refresh, a sync cycle) writes back only through the
/// <c>Try…</c> methods, which do nothing once it has moved.
/// </summary>
public sealed class Account(ISessionStore sessions) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public AccountState State { get; private set; } = new SignedOut(SessionExpired: false);

    public long Generation => Interlocked.Read(ref _generation);

    public event EventHandler? Changed;

    public async Task<AccountState> LoadAsync(CancellationToken cancellationToken)
    {
        Set(From(await sessions.GetAsync(cancellationToken)));
        return State;
    }

    public async Task SignedInAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _generation);
            await sessions.SaveAsync(session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(From(session));
    }

    /// <summary>The tokens go; every local record and the outbox stay (ADR 0003 §4).</summary>
    public async Task SignedOutAsync(bool sessionExpired, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _generation);
            await sessions.ClearAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(new SignedOut(sessionExpired));
    }

    public Task<bool> TrySetPendingRefreshTokenAsync(long generation, string next, CancellationToken cancellationToken) =>
        TryChangeAsync(generation, s => s with { PendingRefreshToken = next }, cancellationToken);

    public Task<bool> TryReplaceTokensAsync(long generation, SessionTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return TryChangeAsync(
            generation,
            s => s with
            {
                AccessToken = tokens.AccessToken,
                AccessTokenExpiresAt = tokens.AccessTokenExpiresAt,
                RefreshToken = tokens.RefreshToken,
                PendingRefreshToken = null,
            },
            cancellationToken);
    }

    /// <summary>The bring-over committed (Spec 011 §4): this phone now shows the account's household.</summary>
    public async Task<bool> TryCompleteJoinAsync(long generation, CancellationToken cancellationToken)
    {
        if (!await TryChangeAsync(generation, s => s with { JoinPending = false }, cancellationToken))
            return false;
        Set(From(await sessions.GetAsync(cancellationToken)));
        return true;
    }

    /// <summary>The server ended the session (only <c>auth.refresh-invalid</c>); after a deliberate sign-out it does nothing.</summary>
    public async Task<bool> TryExpireAsync(long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _generation)
                return false;
            Interlocked.Increment(ref _generation);
            await sessions.ClearAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(new SignedOut(SessionExpired: true));
        return true;
    }

    /// <summary>Settings' refresh of the name and email. Not a sign-in: the generation stays.</summary>
    public async Task UpdateProfileAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StoredSession? updated = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await sessions.GetAsync(cancellationToken) is { } session)
            {
                updated = session with { Profile = profile };
                await sessions.SaveAsync(updated, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (updated is not null)
            Set(From(updated));
    }

    public void Dispose() => _gate.Dispose();

    public static AccountState From(StoredSession? session) => session switch
    {
        null => new SignedOut(SessionExpired: false),
        { JoinPending: true } => new JoinPending(session.Profile, session.AccountHousehold),
        _ => new SignedIn(session.Profile),
    };

    private async Task<bool> TryChangeAsync(long generation, Func<StoredSession, StoredSession> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _generation || await sessions.GetAsync(cancellationToken) is not { } session)
                return false;
            await sessions.SaveAsync(change(session), cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Raised outside the lock: Changed's handlers are not this class's to hold a lock over.
    private void Set(AccountState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
