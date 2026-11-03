using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>
/// Settings' "Sign out": the account's families leave this phone with it (owner, 2026-11-03; amends ADR 0003 §4, where the
/// log stayed). Signed out as one account and in as another, the first account's family and its members stayed on show,
/// labelled "on this phone only". What reached the server comes back at the next sign-in; a family that never synced
/// (the phone's own, logged before signing in) stays. An expired session is not this: then nothing leaves the phone.
/// </summary>
public sealed class SignOutOfPhone(SignOut signOut, Families families, IFamilyRecords records, ISyncControl sync)
{
    /// <summary>How long the last push before signing out may take; offline it gives up and counts what is left.</summary>
    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// For the confirmation: sends what it can first, then how many records of the leaving families have still not reached
    /// the server. Those would be lost with the sign-out, so the confirmation says so.
    /// </summary>
    public async Task<int> UnsentAfterPushAsync(CancellationToken cancellationToken)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(PushTimeout);
            try
            {
                await sync.SyncNowAsync(timeout.Token);
            }
#pragma warning disable CA1031 // Offline, refused or timed out: the count below says what did not get there.
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
            }
        }

        var leaving = await LeavingAsync(cancellationToken);
        return leaving.Count == 0 ? 0 : await records.CountPendingAsync(leaving, cancellationToken);
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Listed before signing out: afterwards the account's memberships are gone, and with them which families are its.
        var leaving = await LeavingAsync(cancellationToken);
        await signOut.ExecuteAsync(cancellationToken);
        JoinFamily.ForgetSuggestedCode();
        if (leaving.Count == 0)
            return;

        await sync.RunExclusiveAsync(
            async token =>
            {
                foreach (var household in leaving)
                    await families.RemoveFromPhoneAsync(household, token);
            },
            cancellationToken);
        sync.NotifyDataChanged();
    }

    /// <summary>The account's families, and any family that was on a server: everything but the phone's own unsynced log.</summary>
    private async Task<IReadOnlyCollection<HouseholdId>> LeavingAsync(CancellationToken cancellationToken) =>
        [.. (await families.ListAsync(cancellationToken)).Where(f => f.IsShared || f.HasEverSynced).Select(f => f.Id)];
}
