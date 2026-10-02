using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

public sealed record BringOverChoice(LocalSummary Local, IReadOnlyList<LocalBaby> Family);

/// <summary>
/// "Bring my entries over" (Spec 011 §4): each local baby to a family baby, or added as new, then one transaction moves
/// everything and the phone shows the family's log. "Keep them separate" is simply not calling it (Q4).
/// </summary>
public sealed class BringEntriesOver(ISessionStore sessions, IHouseholdMoves moves, Account account, ISyncControl sync)
{
    /// <summary>What the sheet shows; null when this phone is not join-pending.</summary>
    public async Task<BringOverChoice?> PrepareAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return null;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        if (local == session.AccountHousehold)
            return null;
        var family = await moves.SummaryAsync(session.AccountHousehold, cancellationToken);
        return new BringOverChoice(await moves.SummaryAsync(local, cancellationToken), family.Babies);
    }

    /// <summary>A target that is not one of the family's babies counts as "Add as a new baby".</summary>
    public async Task<bool> ExecuteAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var generation = account.Generation;
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return false;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        if (local != session.AccountHousehold)
        {
            var family = (await moves.SummaryAsync(session.AccountHousehold, cancellationToken)).Babies.Select(b => b.Id).ToHashSet();
            var babies = (await moves.SummaryAsync(local, cancellationToken)).Babies.ToDictionary(
                b => b.Id,
                b => mapping.TryGetValue(b.Id, out var target) && target is { } t && family.Contains(t) ? t : (BabyId?)null);
            await moves.BringOverAsync(new BringOverPlan(local, session.AccountHousehold, babies), session.Profile.UserId, cancellationToken);
        }

        // Signed out meanwhile: the move stands, and the next sign-in finds this phone already in the account's household.
        if (!await account.TryCompleteJoinAsync(generation, cancellationToken))
            return false;
        sync.RequestSync();
        return true;
    }
}
