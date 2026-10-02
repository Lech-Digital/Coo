using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Auth;

public enum SignInResult
{
    SignedIn,
    Cancelled,
    Failed,
}

/// <summary>
/// The one sign-in (Spec 010 §2, Q3). Settings calls it now; sharing (11b) and Premium (12) call this same use case
/// where the person is, then carry on. A nonce from the server, Google's sheet, then the server's session.
/// </summary>
public sealed class SignIn(
    IAuthApi api, ISignInProvider google, IHouseholdStore households, IDeviceIdentity device, IDeviceName deviceName, Account account)
{
    public async Task<SignInResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nonce = await api.GetNonceAsync(cancellationToken);
            var idToken = await google.GetIdTokenAsync(nonce, cancellationToken);
            if (idToken is null)
                return SignInResult.Cancelled;

            var household = (await households.ListHouseholdsAsync(cancellationToken)).First(h => !h.IsDeleted).Id;
            var grant = await api.SignInWithGoogleAsync(
                new GoogleSignIn(idToken, deviceName.Name, await device.GetAsync(cancellationToken), household), cancellationToken);

            // No local record is re-keyed in any outcome (Spec 010 §3.2): `created` and `joined` are resolved by sync (Spec 011 §4).
            // `existing` with another household: this phone left while join-pending and must still be asked (ruling 9).
            // `created`: the marker names this phone's household, the only one the first sync may move into the account's.
            var joinPending = grant.Outcome == SignInOutcome.Joined
                || (grant.Outcome == SignInOutcome.Existing && grant.Household != household);
            HouseholdId? moveFrom = grant.Outcome == SignInOutcome.Created && grant.Household != household ? household : null;
            await account.SignedInAsync(
                new StoredSession(grant.Tokens.AccessToken, grant.Tokens.AccessTokenExpiresAt, grant.Tokens.RefreshToken, grant.Profile,
                    grant.Household, JoinPending: joinPending, PendingMoveFrom: moveFrom),
                cancellationToken);
            return SignInResult.SignedIn;
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException or SignInUnavailableException)
        {
            return SignInResult.Failed;
        }
    }
}

/// <summary>Sign out on this phone (Spec 010 §2). The server's revoke is best effort; the phone signs out regardless.</summary>
public sealed class SignOut(IAccountApi api, Account account)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.SignOutAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            // Offline or already refused: the server's session ends on its own after 180 idle days.
        }

        await account.SignedOutAsync(sessionExpired: false, cancellationToken);
    }
}

/// <summary>Spec 010 §5 on the phone: the server forgets the account; the local log stays (Q2). False when it failed.</summary>
public sealed class DeleteAccount(IAccountApi api, Account account)
{
    public async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.DeleteAccountAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        await account.SignedOutAsync(sessionExpired: false, cancellationToken);
        return true;
    }
}

/// <summary>
/// Settings opened: the server's name and email replace the stored ones. Offline, nothing changes (deliverable 5); a
/// refused session has already been signed out by the refreshing handler.
/// </summary>
public sealed class RefreshAccount(IAccountApi api, ISessionStore sessions, Account account)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (account.State is SignedOut)
            return;

        AccountOverview me;
        try
        {
            me = await api.GetMeAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return;
        }

        var session = await sessions.GetAsync(cancellationToken);
        if (session is not null && session.Profile != me.Profile)
            await account.UpdateProfileAsync(me.Profile, cancellationToken);
    }
}
