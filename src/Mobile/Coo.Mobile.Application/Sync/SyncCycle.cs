using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// One sync cycle (Spec 011 §6.3, ADR 0002 §7): push, then pull. It captures the account's generation at the start and
/// stops at its next step once it has moved (§8.1). It never runs signed out or without consent; join-pending, it only
/// pulls the account's household (ruling 8); for a soft-locked household it only asks about the membership (§6.5).
/// It throws only for programming errors; network and server answers become a <see cref="CycleReport"/>.
/// </summary>
public sealed class SyncCycle(
    Account account, ISessionStore sessions, ConsentGate consent, ISyncStore store, IHouseholdMoves moves, ISyncState state,
    ISyncApi api, IVersionClock clock, IDeviceIdentity device, TimeProvider time)
{
    public const int PushBatchSize = 200;

    public async Task<CycleReport> RunAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        var session = await sessions.GetAsync(cancellationToken);
        if (session is null || account.State is SignedOut)
            return new CycleReport(CycleOutcome.NotSignedIn);

        var user = session.Profile.UserId;
        switch (await consent.CheckAsync(user, cancellationToken))
        {
            case ConsentState.Declined:
                return new CycleReport(CycleOutcome.ConsentDeclined);
            case ConsentState.Unknown:
                return new CycleReport(CycleOutcome.ConsentNeeded);
        }

        var household = session.AccountHousehold;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        var changed = false;
        try
        {
            if (session.JoinPending)
            {
                if (local == household)
                {
                    // The bring-over committed, but the app stopped before the session said so.
                    await account.TryCompleteJoinAsync(generation, cancellationToken);
                    return new CycleReport(CycleOutcome.Interrupted);
                }

                var pulled = await PullAllAsync(household, generation, cancellationToken);
                return new CycleReport(pulled.Interrupted ? CycleOutcome.Interrupted : CycleOutcome.JoinPending, pulled.Changed);
            }

            if (await state.IsSoftLockedAsync(household, cancellationToken))
                return await CheckMembershipAsync(household, cancellationToken);

            if (local != household)
            {
                // `created` (Spec 011 §4): the account's household is new and empty; everything here moves into it.
                await moves.MoveAllAsync(local, household, user, cancellationToken);
                changed = true;
            }

            if (Moved(generation))
                return new CycleReport(CycleOutcome.Interrupted, changed);

            await store.PrepareForPushAsync(household, user, cancellationToken);
            var deviceId = await device.GetAsync(cancellationToken);
            while (true)
            {
                if (Moved(generation))
                    return new CycleReport(CycleOutcome.Interrupted, changed);

                var batch = await store.NextBatchAsync(household, PushBatchSize, cancellationToken);
                if (batch.Count == 0)
                    break;

                var results = await api.PushAsync(household, deviceId, batch, cancellationToken);
                await store.ApplyResultsAsync(household, batch, results, time.GetUtcNow(), cancellationToken);

                // A change the server did not answer stays in the outbox; pushing again now would send it again at once.
                var answered = results.Select(r => r.ChangeId).ToHashSet();
                if (batch.Any(c => !answered.Contains(c.ChangeId)))
                    return new CycleReport(CycleOutcome.Failed, changed, "undecided");
                if (batch.Count < PushBatchSize)
                    break;
            }

            var pull = await PullAllAsync(household, generation, cancellationToken);
            return new CycleReport(pull.Interrupted ? CycleOutcome.Interrupted : CycleOutcome.Synced, changed || pull.Changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.MembershipRevoked && e.Household == household)
        {
            // ADR 0002 §10 step 2: soft-lock first; delete nothing.
            await state.SetSoftLockAsync(household, true, cancellationToken);
            return new CycleReport(CycleOutcome.SoftLocked, changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ConsentRequired)
        {
            await consent.ForgetAsync(user, cancellationToken);
            return new CycleReport(CycleOutcome.ConsentNeeded, changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ClientTooOld)
        {
            return new CycleReport(CycleOutcome.ClientTooOld, changed, e.Refusal.ToString());
        }
        catch (SyncRefusedException e)
        {
            return new CycleReport(CycleOutcome.Failed, changed, e.Refusal.ToString());
        }
        catch (SyncUnavailableException)
        {
            return new CycleReport(CycleOutcome.Failed, changed, nameof(SyncUnavailableException));
        }
    }

    private bool Moved(long generation) => account.Generation != generation;

    private async Task<CycleReport> CheckMembershipAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        switch (await api.GetMembershipAsync(household, cancellationToken))
        {
            case MembershipStatus.Active:
                await state.SetSoftLockAsync(household, false, cancellationToken);
                return new CycleReport(CycleOutcome.Interrupted);
            case MembershipStatus.Revoked:
                // Confirmed. Deleting waits for the person (ADR 0002 §10; ruling 13).
                return new CycleReport(CycleOutcome.Removed, Household: household);
            default:
                return new CycleReport(CycleOutcome.SoftLocked);
        }
    }

    private async Task<(bool Changed, bool Interrupted)> PullAllAsync(HouseholdId household, long generation, CancellationToken cancellationToken)
    {
        var changed = false;
        while (true)
        {
            if (Moved(generation))
                return (changed, true);

            var cursor = await store.GetCursorAsync(household, cancellationToken);
            var page = await api.PullAsync(household, cursor.Cursor, cursor.BootstrapHead, cancellationToken);
            if (page.HasMore && page.Cursor <= cursor.Cursor)
                throw new SyncUnavailableException("The server reported more records without moving the cursor.");

            // A page for an account this phone no longer holds is not applied.
            if (Moved(generation))
                return (changed, true);

            await store.ApplyPageAsync(household, page, cancellationToken);
            if (page.Records.Count > 0)
            {
                changed = true;
                await clock.ObserveAsync(page.Records.Max(r => r.Version), cancellationToken);
            }

            if (!page.HasMore)
                return (changed, false);
        }
    }
}
