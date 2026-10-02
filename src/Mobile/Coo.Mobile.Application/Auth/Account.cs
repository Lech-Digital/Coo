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
        await ChangeAsync(async () => Apply(From(await sessions.GetAsync(cancellationToken))), cancellationToken);
        return State;
    }

    public Task SignedInAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ChangeAsync(
            async () =>
            {
                Interlocked.Increment(ref _generation);
                await sessions.SaveAsync(session, cancellationToken);
                return Apply(From(session));
            },
            cancellationToken);
    }

    /// <summary>The tokens go; every local record and the outbox stay (ADR 0003 §4).</summary>
    public Task SignedOutAsync(bool sessionExpired, CancellationToken cancellationToken) => ChangeAsync(
        async () =>
        {
            Interlocked.Increment(ref _generation);
            await sessions.ClearAsync(cancellationToken);
            return Apply(new SignedOut(sessionExpired));
        },
        cancellationToken);

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
    public Task<bool> TryCompleteJoinAsync(long generation, CancellationToken cancellationToken) =>
        TryChangeAsync(generation, s => s with { JoinPending = false }, cancellationToken);

    /// <summary>The server ended the session (only <c>auth.refresh-invalid</c>); after a deliberate sign-out it does nothing.</summary>
    public async Task<bool> TryExpireAsync(long generation, CancellationToken cancellationToken)
    {
        var expired = false;
        await ChangeAsync(
            async () =>
            {
                if (generation != _generation)
                    return false;
                Interlocked.Increment(ref _generation);
                await sessions.ClearAsync(cancellationToken);
                expired = true;
                return Apply(new SignedOut(SessionExpired: true));
            },
            cancellationToken);
        return expired;
    }

    /// <summary>
    /// Settings' refresh of the name and email. Not a sign-in: the generation stays. Only the same account's session takes
    /// it: an answer that arrives after signing in as someone else changes nothing.
    /// </summary>
    public Task UpdateProfileAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ChangeAsync(
            async () =>
            {
                if (await sessions.GetAsync(cancellationToken) is not { } session || session.Profile.UserId != profile.UserId)
                    return false;
                var updated = session with { Profile = profile };
                await sessions.SaveAsync(updated, cancellationToken);
                return Apply(From(updated));
            },
            cancellationToken);
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
        var changed = false;
        await ChangeAsync(
            async () =>
            {
                if (generation != _generation || await sessions.GetAsync(cancellationToken) is not { } session)
                    return false;
                var updated = change(session);
                await sessions.SaveAsync(updated, cancellationToken);
                changed = true;
                return Apply(From(updated));
            },
            cancellationToken);
        return changed;
    }

    /// <summary>
    /// Runs <paramref name="change"/> under the lock, so the store and <see cref="State"/> move together; it returns whether
    /// the state changed. <see cref="Changed"/> is raised after the lock is released: its handlers are not this class's to
    /// hold a lock over.
    /// </summary>
    private async Task ChangeAsync(Func<Task<bool>> change, CancellationToken cancellationToken)
    {
        bool stateChanged;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            stateChanged = await change();
        }
        finally
        {
            _gate.Release();
        }

        if (stateChanged)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called under the lock only.</summary>
    private bool Apply(AccountState state)
    {
        if (state == State)
            return false;
        State = state;
        return true;
    }
}
