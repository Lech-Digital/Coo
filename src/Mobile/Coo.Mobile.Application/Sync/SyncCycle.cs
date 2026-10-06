using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// One sync cycle (Spec 011 §6.3, Spec 011b §7.2): the home family first, then every other account family in joined order,
/// each with its own soft-lock check, push and pull. It captures the account's generation at the start and stops at its next
/// step once it has moved (Spec 011 §8.1). It never runs signed out or without consent; join-pending, it only pulls the home
/// family (Spec 011 ruling 8). Records move into the home family only as the <c>created</c> sign-in's marker names (Spec 011
/// §4), and never mid-join (ruling 4). Mid-join, nothing is pushed or pulled for a family that has never synced (§5.3,
/// ruling 5); a shown family that is not an account family is never pushed (ruling 1). The home family follows the
/// account's memberships (§7.1, §8.4). A family on the phone that has synced but is missing from the account's list is
/// only asked about (<c>/membership</c>), so a removal found by a refresh is still confirmed (§8.2). It throws only for programming errors; network and server answers become a
/// <see cref="CycleReport"/>.
/// </summary>
public sealed class SyncCycle(
    Account account, ISessionStore sessions, ConsentGate consent, ISyncStore store, IHouseholdMoves moves, ISyncState state,
    ISyncApi api, IVersionClock clock, IDeviceIdentity device, TimeProvider time, IFamilyStore families, IHouseholdApi households,
    SyncStops stops, IHouseholdStore onPhone)
{
    public const int PushBatchSize = 200;

    /// <summary>No longer produced (Spec 011b ruling 1); kept so a stored status from 0.11.0 still reads.</summary>
    public const string HouseholdMismatchError = "household-mismatch";

    public const string NoProgressError = "no-progress";

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

        var run = new Run(generation, user, stops.Wake);
        try
        {
            var local = await moves.ActiveHouseholdAsync(cancellationToken);

            // Consent and the active household were awaited: a sign-out meanwhile ends the cycle before anything is written.
            if (Moved(run))
                return new CycleReport(CycleOutcome.Interrupted);

            var memberships = await families.ListAsync(user, cancellationToken);
            var joining = await families.GetJoiningAsync(cancellationToken);
            var midJoin = joining is not null || await families.GetPendingInvitationAsync(cancellationToken) is not null;

            var (home, created) = await EnsureHomeAsync(session, local, memberships, midJoin, run, cancellationToken);
            if (run.Interrupted)
                return run.Report();

            // A family created on the server just now is the shown one (re-keyed or not) and is not in the list yet.
            if (created)
                local = home;
            var homeActive = created || memberships is null || session.JoinPending || memberships.Any(m => m.Household == home);
            if (homeActive && memberships is null && !session.JoinPending && session.PendingMoveFrom is null
                && !(await onPhone.ListHouseholdsAsync(cancellationToken)).Any(h => h.Id == home && !h.IsDeleted))
            {
                // Review M-d: the home was taken off this phone (an acknowledged removal) and no list says it is still
                // the account's. Nothing is pushed, pulled or soft-locked for it; the next refresh moves the home (§7.1).
                homeActive = false;
            }

            if (homeActive && !stops.IsStopped(home))
                await HomeStepAsync(session, home, local, midJoin, joining, run, cancellationToken);

            foreach (var family in Others(memberships, home, joining))
            {
                if (run.Interrupted || Moved(run))
                {
                    run.Interrupted = true;
                    break;
                }

                if (stops.IsStopped(family))
                    continue;
                if (midJoin && family != joining?.Joined && !await moves.HasEverSyncedAsync(family, cancellationToken))
                    continue;
                if (await state.IsSoftLockedAsync(family, cancellationToken))
                {
                    await CheckMembershipAsync(family, shown: family == local, run, cancellationToken);
                    continue;
                }

                await SyncFamilyAsync(family, shown: family == local, run, cancellationToken);
            }

            // After the account families: confirming a removal never holds up syncing the others.
            if (!run.Interrupted && memberships is not null)
                await ConfirmDroppedFamiliesAsync(memberships, joining, created ? home : null, local, run, cancellationToken);

            return run.Report();
        }
        catch (StoppedException e)
        {
            return run.Report(CycleOutcome.Failed, e.Message);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ConsentRequired)
        {
            await consent.ForgetAsync(user, cancellationToken);
            return run.Report(CycleOutcome.ConsentNeeded);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ClientTooOld)
        {
            return run.Report(CycleOutcome.ClientTooOld, e.Refusal.ToString());
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.Malformed)
        {
            // `sync.malformed` and `sync.batch-too-large`: retrying sends the same request (Spec 011b §9, ruling 21).
            return run.Report(CycleOutcome.Failed, NoProgressError);
        }
        catch (SyncRefusedException e)
        {
            return run.Report(CycleOutcome.Failed, e.Refusal.ToString());
        }
        catch (SyncUnavailableException)
        {
            return run.Report(CycleOutcome.Failed, nameof(SyncUnavailableException));
        }
    }

    /// <summary>
    /// Spec 011b §8.2 (review C1): <c>/api/me</c> lists active memberships only, so a refresh drops a family the person was
    /// removed from, before or after its soft-lock. Each family on the phone that has synced and that the known list lacks
    /// is asked about, never pushed or pulled: <c>revoked</c> reports its removal (the first only, ruling 23); <c>active</c>
    /// means the list is stale; anything else (another account's family answers not-a-member) leaves it alone, except that a
    /// shown family already soft-locked stays paused for the sync line (review I2, spec §9).
    /// </summary>
    private async Task ConfirmDroppedFamiliesAsync(
        IReadOnlyList<FamilyMembership> memberships, JoiningMarker? joining, HouseholdId? created, HouseholdId shown, Run run,
        CancellationToken cancellationToken)
    {
        // The joined family and a family created this cycle are account families the list does not know yet.
        var known = memberships.Select(m => m.Household).ToHashSet();
        if (joining is not null)
            known.Add(joining.Joined);
        if (created is { } family)
            known.Add(family);

        foreach (var household in await onPhone.ListHouseholdsAsync(cancellationToken))
        {
            if (run.Removed is not null)
                return;
            if (household.IsDeleted || known.Contains(household.Id) || !await moves.HasEverSyncedAsync(household.Id, cancellationToken))
                continue;

            var status = await api.GetMembershipAsync(household.Id, cancellationToken);
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            if (status == MembershipStatus.Revoked)
                run.Removed = household.Id;
            else if (status == MembershipStatus.Active)
                run.RefreshFamilies = true;
            else if (household.Id == shown && await state.IsSoftLockedAsync(household.Id, cancellationToken))
                run.ShownSoftLocked = true;
        }
    }

    /// <summary>The account's other families, in joined order, and the joined family when the list does not have it yet.</summary>
    private static List<HouseholdId> Others(IReadOnlyList<FamilyMembership>? memberships, HouseholdId home, JoiningMarker? joining)
    {
        var others = (memberships ?? []).Select(m => m.Household).Where(h => h != home).ToList();
        if (joining is not null && joining.Joined != home && !others.Contains(joining.Joined))
            others.Add(joining.Joined);
        return others;
    }

    /// <summary>
    /// Spec 011b §7.1 and §8.4 (ruling 7): the home family follows the account's memberships. Returns the home family, and
    /// whether this cycle just created it on the server.
    /// </summary>
    private async Task<(HouseholdId Home, bool Created)> EnsureHomeAsync(
        StoredSession session, HouseholdId local, IReadOnlyList<FamilyMembership>? memberships, bool midJoin, Run run,
        CancellationToken cancellationToken)
    {
        var home = session.AccountHousehold;
        if (memberships is null || session.JoinPending || memberships.Any(m => m.Household == home))
            return (home, false);

        if (memberships.Count > 0)
        {
            // The list is earliest-joined first.
            var next = memberships[0].Household;
            if (!await account.TrySetHomeAsync(run.Generation, next, cancellationToken))
                run.Interrupted = true;
            return (next, false);
        }

        // No active family on the server. Mid-join the joined family becomes one (§5.3); otherwise the shown family does,
        // if it has never been on a server.
        if (midJoin || await moves.HasEverSyncedAsync(local, cancellationToken))
            return (home, false);

        var target = local;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // A family created under an account this phone no longer holds would belong to that account.
            if (Moved(run))
            {
                run.Interrupted = true;
                return (home, false);
            }

            try
            {
                await households.CreateHouseholdAsync(target, cancellationToken);
            }
            catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.IdTaken && attempt == 0)
            {
                if (Moved(run))
                {
                    run.Interrupted = true;
                    return (home, false);
                }

                var fresh = new HouseholdId(Guid.CreateVersion7(time.GetUtcNow()));

                // A household that ever synced is never re-keyed. Unreachable in practice: the shown family was found never-synced
                // above, and no cycle syncs a family of no account. Were it refused, the store would move nothing and make the
                // fresh ID active with a copy of the settings; it is not created on the server, so the next cycle, finding the
                // fresh family never-synced and shown, creates that one instead.
                if (!await moves.MoveAllAsync(target, fresh, run.User, cancellationToken))
                    return (home, false);
                run.Changed = true;
                target = fresh;
                continue;
            }
            catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.AlreadyHasFamily)
            {
                // Invited back meanwhile: the list learns the family, and this one stays on the phone only.
                run.RefreshFamilies = true;
                return (home, false);
            }
            catch (HouseholdRefusedException e)
            {
                throw new SyncUnavailableException("The family could not be created: " + e.Code, e);
            }
            catch (HouseholdUnavailableException e)
            {
                throw new SyncUnavailableException("The family could not be created now.", e);
            }

            if (!await account.TrySetHomeAsync(run.Generation, target, cancellationToken))
            {
                run.Interrupted = true;
                return (home, false);
            }

            // It syncs in this cycle (§8.4); the list does not know it yet. Waiting for another cycle instead would send the
            // create again, which the server answers with success again (a retried create), until the list was refreshed.
            run.RefreshFamilies = true;
            return (target, true);
        }

        // Unreachable: the second id-taken is refused above.
        return (home, false);
    }

    private async Task HomeStepAsync(
        StoredSession session, HouseholdId home, HouseholdId local, bool midJoin, JoiningMarker? joining, Run run, CancellationToken cancellationToken)
    {
        if (await state.IsSoftLockedAsync(home, cancellationToken))
        {
            await CheckMembershipAsync(home, shown: home == local || session.JoinPending, run, cancellationToken);
            return;
        }

        if (session.JoinPending)
        {
            if (local == home)
            {
                // The bring-over committed, but the app stopped before the session said so.
                await account.TryCompleteJoinAsync(run.Generation, cancellationToken);
                run.Interrupted = true;
                return;
            }

            // Ruling 20: the home family while join-pending speaks for the sync line.
            run.HomeJoinPending = true;
            await WithFamilyRefusalsAsync(home, shown: true, run, () => PullAllAsync(home, run, cancellationToken), cancellationToken);
            return;
        }

        // Spec 011b §5.3: nothing of a never-synced family moves to a server mid-join; its pull would give it a cursor (ruling 5).
        if (midJoin && joining?.Joined != home && !await moves.HasEverSyncedAsync(home, cancellationToken))
            return;

        if (session.PendingMoveFrom is { } from && !midJoin)
        {
            // The marker names a move into the family the `created` sign-in returned, never into a home that replaced it.
            if (from == local && local != home && home == session.AccountHousehold)
            {
                if (Moved(run))
                {
                    run.Interrupted = true;
                    return;
                }

                // A household that ever synced is never moved: the account's household simply shows instead.
                await moves.MoveAllAsync(local, home, run.User, cancellationToken);
                run.Changed = true;
            }

            await account.TryClearPendingMoveAsync(run.Generation, cancellationToken);
        }

        await SyncFamilyAsync(home, shown: home == local, run, cancellationToken);
    }

    /// <summary>Push, then pull, for one family; this family's own refusals are handled and the cycle goes on.</summary>
    private Task SyncFamilyAsync(HouseholdId household, bool shown, Run run, CancellationToken cancellationToken) =>
        WithFamilyRefusalsAsync(household, shown, run, () => PushThenPullAsync(household, run, cancellationToken), cancellationToken);

    /// <summary>
    /// The refusals that name this family: <c>membership-revoked</c> soft-locks it and <c>not-a-member</c> stops it until a
    /// wake (spec §7.2, §9); every other answer ends the cycle.
    /// </summary>
    private async Task WithFamilyRefusalsAsync(HouseholdId household, bool shown, Run run, Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step();
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.MembershipRevoked && e.Household == household)
        {
            // ADR 0002 §10 step 2: soft-lock first; delete nothing. Not for an account this phone no longer holds.
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            await state.SetSoftLockAsync(household, true, cancellationToken);
            run.RefreshFamilies = true;
            if (shown)
                run.ShownSoftLocked = true;
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.NotAMember && e.Household == household)
        {
            // The stop belongs to this account's families: not for an account this phone no longer holds.
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            stops.Stop(household, run.Wake);
            run.RefreshFamilies = true;
            run.NotAMember.Add(household);
        }
    }

    private async Task PushThenPullAsync(HouseholdId household, Run run, CancellationToken cancellationToken)
    {
        if (Moved(run))
        {
            run.Interrupted = true;
            return;
        }

        await store.PrepareForPushAsync(household, run.User, cancellationToken);
        var deviceId = await device.GetAsync(cancellationToken);
        Guid? previousFirst = null;
        while (true)
        {
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            var batch = await store.NextBatchAsync(household, PushBatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            // Every change of the last batch was answered, so the store must have moved on; if not, stop rather than loop.
            if (batch[0].ChangeId == previousFirst)
                throw new StoppedException(NoProgressError);
            previousFirst = batch[0].ChangeId;

            var results = await api.PushAsync(household, deviceId, batch, cancellationToken);
            await store.ApplyResultsAsync(household, batch, results, time.GetUtcNow(), cancellationToken);

            // A change the server did not answer stays in the outbox; pushing again now would send it again at once, and
            // retrying cannot fix it (Spec 011b §9, ruling 21).
            var answered = results.Select(r => r.ChangeId).ToHashSet();
            if (batch.Any(c => !answered.Contains(c.ChangeId)))
                throw new StoppedException(NoProgressError);
            if (batch.Count < PushBatchSize)
                break;
        }

        await PullAllAsync(household, run, cancellationToken);
    }

    private bool Moved(Run run) => account.Generation != run.Generation;

    private async Task CheckMembershipAsync(HouseholdId household, bool shown, Run run, CancellationToken cancellationToken)
    {
        var status = await api.GetMembershipAsync(household, cancellationToken);
        if (Moved(run))
        {
            run.Interrupted = true;
            return;
        }

        switch (status)
        {
            case MembershipStatus.Active:
                // Lifted: the next cycle syncs at once rather than at the next 30-second tick.
                await state.SetSoftLockAsync(household, false, cancellationToken);
                (run.Interrupted, run.RunAgain) = (true, true);
                return;
            case MembershipStatus.Revoked:
                // Confirmed. Deleting waits for the person (ADR 0002 §10; the removal sheet, Spec 011b §8.2).
                run.Removed ??= household;
                return;
            default:
                run.RefreshFamilies = true;
                if (shown)
                    run.ShownSoftLocked = true;
                return;
        }
    }

    private async Task PullAllAsync(HouseholdId household, Run run, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            var cursor = await store.GetCursorAsync(household, cancellationToken);
            var page = await api.PullAsync(household, cursor.Cursor, cursor.BootstrapHead, cancellationToken);
            if (page.HasMore && page.Cursor <= cursor.Cursor)
                throw new SyncUnavailableException("The server reported more records without moving the cursor.");

            // A page for an account this phone no longer holds is not applied.
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            // Observe first: a crash after the page commits must not leave the clock behind a stored stamp, or the next
            // local edit would lose to it. Observing a page that is then not applied is harmless.
            if (page.Records.Count > 0)
            {
                run.Changed = true;
                await clock.ObserveAsync(page.Records.Max(r => r.Version), cancellationToken);
            }

            await store.ApplyPageAsync(household, page, cancellationToken);
            if (!page.HasMore)
                return;
        }
    }

    /// <summary>What one cycle found across its families; the outcome follows ruling 20's precedence.</summary>
    private sealed class Run(long generation, UserId user, long wake)
    {
        public long Generation { get; } = generation;

        /// <summary>The wake this cycle began under: its not-a-member stops do not outlive a later one (review M-a).</summary>
        public long Wake { get; } = wake;

        public UserId User { get; } = user;

        public bool Changed { get; set; }

        public bool Interrupted { get; set; }

        public bool RunAgain { get; set; }

        public bool RefreshFamilies { get; set; }

        public bool HomeJoinPending { get; set; }

        public bool ShownSoftLocked { get; set; }

        public HouseholdId? Removed { get; set; }

        public List<HouseholdId> NotAMember { get; } = [];

        public CycleReport Report()
        {
            var outcome = Interrupted ? CycleOutcome.Interrupted
                : Removed is not null ? CycleOutcome.Removed
                : ShownSoftLocked ? CycleOutcome.SoftLocked
                : HomeJoinPending ? CycleOutcome.JoinPending
                : CycleOutcome.Synced;
            return new CycleReport(outcome, Changed, Household: Removed, RunAgain: RunAgain, RefreshFamilies: RefreshFamilies,
                NotAMember: NotAMember.Count > 0 ? [.. NotAMember] : null);
        }

        /// <summary>A cycle-wide end (review I2): what the families before it found (a refresh, a stop) still travels.</summary>
        public CycleReport Report(CycleOutcome outcome, string? error = null) =>
            new(outcome, Changed, error, RefreshFamilies: RefreshFamilies, NotAMember: NotAMember.Count > 0 ? [.. NotAMember] : null);
    }

    /// <summary>A stop that retrying cannot fix: the cycle reports it as Failed with its message as the error.</summary>
    private sealed class StoppedException : Exception
    {
        public StoppedException()
            : this(NoProgressError)
        {
        }

        public StoppedException(string message)
            : base(message)
        {
        }

        public StoppedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
