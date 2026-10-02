using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// The consent of Spec 011 §3, per account: cached on the phone, recorded on the server, read back from <c>/api/me</c> so
/// the account's second phone does not ask again. Without it the engine neither pushes nor pulls (Q8).
/// </summary>
public sealed class ConsentGate(ISyncState state, IAccountApi accounts, IConsentApi api)
{
    public async Task<ConsentState> CheckAsync(UserId user, CancellationToken cancellationToken)
    {
        var cached = await state.GetConsentAsync(user, cancellationToken);
        if (cached != ConsentState.Unknown)
            return cached;

        try
        {
            var me = await accounts.GetMeAsync(cancellationToken);
            if (me.Consent?.Wording != CareDataConsent.CurrentWording)
                return ConsentState.Unknown;
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return ConsentState.Unknown;
        }

        await state.SetConsentAsync(user, ConsentState.Given, cancellationToken);
        return ConsentState.Given;
    }

    /// <summary>Agree and share. False when the server could not record it (offline): nothing is cached then.</summary>
    public async Task<bool> AgreeAsync(UserId user, string appVersion, string language, CancellationToken cancellationToken)
    {
        try
        {
            await api.RecordAsync(appVersion, language, cancellationToken);
        }
        catch (Exception e) when (e is SyncUnavailableException or SyncRefusedException or AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        await state.SetConsentAsync(user, ConsentState.Given, cancellationToken);
        return true;
    }

    /// <summary>Not now: remembered until the next sign-in (ruling 10).</summary>
    public Task DeclineAsync(UserId user, CancellationToken cancellationToken) => state.SetConsentAsync(user, ConsentState.Declined, cancellationToken);

    /// <summary>The server asked again (a new wording): the cached consent no longer counts.</summary>
    public Task ForgetAsync(UserId user, CancellationToken cancellationToken) => state.SetConsentAsync(user, ConsentState.Unknown, cancellationToken);

    /// <summary>A new sign-in asks again after a "Not now"; a given consent stays.</summary>
    public async Task ResetDeclinedAsync(UserId user, CancellationToken cancellationToken)
    {
        if (await state.GetConsentAsync(user, cancellationToken) == ConsentState.Declined)
            await state.SetConsentAsync(user, ConsentState.Unknown, cancellationToken);
    }
}
