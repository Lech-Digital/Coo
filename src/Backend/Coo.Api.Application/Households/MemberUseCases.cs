using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

public sealed record MembersView(IReadOnlyList<MemberProfile> Members, IReadOnlyList<OpenInvitation> Invitations);

/// <summary><c>GET /api/households/{id}/members</c>: any active member; invitations only for an Owner or Parent.</summary>
public sealed class ListMembers(IHouseholdMembershipStore store, TimeProvider time)
{
    public async Task<Result<MembersView>> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken)
    {
        var mine = await store.ListActiveMembershipsOfUserAsync(user, cancellationToken);
        if (mine.FirstOrDefault(m => m.HouseholdId == household) is not { } me)
            return Result<MembersView>.Failure(HouseholdErrors.Forbidden);
        var members = await store.ListActiveMembersAsync(household, cancellationToken);
        IReadOnlyList<OpenInvitation> invitations = me.IsParent
            ? await store.ListOpenInvitationsAsync(household, time.GetUtcNow(), cancellationToken)
            : [];
        return new MembersView(members, invitations);
    }
}

/// <summary>
/// <c>DELETE /api/households/{id}/members/{userId}</c> (spec §8.1): the membership is revoked (the row stays, so ADR 0002
/// §10's answer works) and the removed member's open invitations for the family are withdrawn, in one transaction.
/// </summary>
public sealed class RemoveMember(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, UserId target, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { IsParent: true } remover || me.IsRevoked)
                return Result.Failure(HouseholdErrors.Forbidden);
            var them = await tx.FindMembershipAsync(household, target, cancellationToken);
            if (them.Membership is not { } removed || them.IsRevoked)
                return Result.Failure(HouseholdErrors.MemberNotFound);
            if (!remover.CanRemove(removed))
                return Result.Failure(HouseholdErrors.Forbidden);

            var now = time.GetUtcNow();
            await tx.RevokeMembershipAsync(household, target, now, cancellationToken);
            await tx.WithdrawOpenInvitationsByAsync(household, target, now, cancellationToken);
            return Result.Success;
        }, cancellationToken);
}
