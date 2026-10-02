using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

public sealed record BringOverChoice(LocalSummary Local, IReadOnlyList<LocalBaby> Family);

/// <summary>What <see cref="BringEntriesOver.ExecuteAsync"/> did; the sheet's message follows from it.</summary>
public enum BringOverResult
{
    /// <summary>The household is the account's and the join is complete (also when the move was refused).</summary>
    Done,

    /// <summary>Every path that returned before the move committed: nothing on the phone changed.</summary>
    NothingChanged,

    /// <summary>The move committed, but the join could not be completed (signed out meanwhile, or a failing save).</summary>
    MovedButNotCompleted,
}

/// <summary>
/// "Bring my entries over" (Spec 011 §4): each local baby to a family baby, or added as new, then one transaction moves
/// everything and the phone shows the family's log. "Keep them separate" is simply not calling it (Q4).
/// </summary>
public sealed class BringEntriesOver(ISessionStore sessions, IHouseholdMoves moves, Account account, ISyncControl sync)
{
    /// <summary>What the sheet shows; null when this phone is not join-pending.</summary>
    public async Task<BringOverChoice?> PrepareAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return null;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        if (local == session.AccountHousehold)
            return null;

        // A household that has synced is never offered (Spec 011 §4): the account's household simply becomes active.
        if (await moves.HasEverSyncedAsync(local, cancellationToken))
        {
            await CompleteWithoutMoveAsync(session, generation, cancellationToken);
            return null;
        }

        var family = await moves.SummaryAsync(session.AccountHousehold, cancellationToken);
        return new BringOverChoice(await moves.SummaryAsync(local, cancellationToken), family.Babies);
    }

    /// <summary>A target that is not one of the family's babies counts as "Add as a new baby". A failing move throws (rolled back).</summary>
    public async Task<BringOverResult> ExecuteAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var generation = account.Generation;
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return BringOverResult.NothingChanged;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        var moved = false;
        if (local != session.AccountHousehold)
        {
            var family = (await moves.SummaryAsync(session.AccountHousehold, cancellationToken)).Babies.Select(b => b.Id).ToHashSet();
            var babies = (await moves.SummaryAsync(local, cancellationToken)).Babies.ToDictionary(
                b => b.Id,
                b => mapping.TryGetValue(b.Id, out var target) && target is { } t && family.Contains(t) ? t : (BabyId?)null);
            if (account.Generation != generation)
                return BringOverResult.NothingChanged;
            await moves.BringOverAsync(new BringOverPlan(local, session.AccountHousehold, babies), session.Profile.UserId, cancellationToken);
            moved = true;
        }

        try
        {
            // Signed out meanwhile: the move stands, and the next sign-in finds this phone already in the account's household.
            if (!await account.TryCompleteJoinAsync(generation, cancellationToken))
                return moved ? BringOverResult.MovedButNotCompleted : BringOverResult.NothingChanged;
            sync.RequestSync();
            return BringOverResult.Done;
        }
#pragma warning disable CA1031 // After the commit nothing may claim "nothing changed"; before it, the failure propagates.
        catch (Exception) when (moved)
#pragma warning restore CA1031
        {
            return BringOverResult.MovedButNotCompleted;
        }
    }

    /// <summary>Nothing moves: the account's household only becomes active, then the join completes.</summary>
    private async Task CompleteWithoutMoveAsync(StoredSession session, long generation, CancellationToken cancellationToken)
    {
        if (account.Generation != generation)
            return;
        await moves.ActivateAsync(session.AccountHousehold, cancellationToken);
        if (await account.TryCompleteJoinAsync(generation, cancellationToken))
            sync.RequestSync();
    }
}
