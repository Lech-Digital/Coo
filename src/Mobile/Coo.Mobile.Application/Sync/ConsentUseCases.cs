using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>Agree and share (Spec 011 §3): recorded on the server, then a cycle. False when it could not be recorded.</summary>
public sealed class RecordConsent(ISessionStore sessions, ConsentGate consent, ISyncControl sync)
{
    public async Task<bool> ExecuteAsync(string appVersion, string language, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return false;
        if (!await consent.AgreeAsync(session.Profile.UserId, appVersion, language, cancellationToken))
            return false;
        sync.RequestSync();
        return true;
    }
}

/// <summary>Not now, or Back: nothing is sent, the person stays signed in, the sync line offers Turn on sharing.</summary>
public sealed class DeclineConsent(ISessionStore sessions, ConsentGate consent, ISyncControl sync)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return;
        await consent.DeclineAsync(session.Profile.UserId, cancellationToken);
        sync.RequestSync();
    }
}
