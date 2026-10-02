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
/// (ADR 0003 §4), and nothing here touches local care data.
/// </summary>
public sealed class Account(ISessionStore sessions)
{
    public AccountState State { get; private set; } = new SignedOut(SessionExpired: false);

    public event EventHandler? Changed;

    public async Task<AccountState> LoadAsync(CancellationToken cancellationToken)
    {
        Set(From(await sessions.GetAsync(cancellationToken)));
        return State;
    }

    public async Task SignedInAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await sessions.SaveAsync(session, cancellationToken);
        Set(From(session));
    }

    /// <summary>The tokens go; every local record and the outbox stay (ADR 0003 §4).</summary>
    public async Task SignedOutAsync(bool sessionExpired, CancellationToken cancellationToken)
    {
        await sessions.ClearAsync(cancellationToken);
        Set(new SignedOut(sessionExpired));
    }

    public static AccountState From(StoredSession? session) => session switch
    {
        null => new SignedOut(SessionExpired: false),
        { JoinPending: true } => new JoinPending(session.Profile, session.AccountHousehold),
        _ => new SignedIn(session.Profile),
    };

    private void Set(AccountState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
