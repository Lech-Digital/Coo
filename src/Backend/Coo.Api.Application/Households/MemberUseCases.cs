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

public enum LeaveOutcome
{
    Left,
    Deleted,
}

/// <summary>
/// <c>POST /api/households/{id}/leave</c> (spec §8.3). Idempotent: a revoked membership answers <c>left</c> again, a family
/// that no longer exists answers <c>deleted</c> (ruling 15). <c>confirm: false</c> deletes an Owner-only family only when
/// it holds no record.
/// </summary>
public sealed class LeaveHousehold(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result<LeaveOutcome>> ExecuteAsync(UserId user, HouseholdId household, bool confirm, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result<LeaveOutcome>.Ok(LeaveOutcome.Deleted);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is null)
                return Result<LeaveOutcome>.Failure(HouseholdErrors.Forbidden);
            if (me.IsRevoked)
                return Result<LeaveOutcome>.Ok(LeaveOutcome.Left);

            var members = await tx.ActiveMembersAsync(household, cancellationToken);
            var hasOther = (await tx.ActiveMembershipsOfUserAsync(user, cancellationToken)).Any(m => m.HouseholdId != household);
            switch (HouseholdLeaving.Decide(members, user, hasOther))
            {
                case LeaveDecision.RefusedOwnerWithMembers:
                    return Result<LeaveOutcome>.Failure(HouseholdErrors.OwnerCannotLeave);
                case LeaveDecision.RefusedLastFamily:
                    return Result<LeaveOutcome>.Failure(HouseholdErrors.LastFamily);
                case LeaveDecision.HouseholdDeleted:
                    if (!confirm && await tx.HoldsAnyRecordAsync(household, cancellationToken))
                        return Result<LeaveOutcome>.Failure(HouseholdErrors.ConfirmationRequired);
                    await tx.DeleteHouseholdAsync(household, cancellationToken);
                    return Result<LeaveOutcome>.Ok(LeaveOutcome.Deleted);
                default:
                    var now = time.GetUtcNow();
                    await tx.RevokeMembershipAsync(household, user, now, cancellationToken);
                    await tx.WithdrawOpenInvitationsByAsync(household, user, now, cancellationToken);
                    return Result<LeaveOutcome>.Ok(LeaveOutcome.Left);
            }
        }, cancellationToken);
}

/// <summary>
/// <c>POST /api/households</c> (spec §8.4): a user with no active family gets one with the phone's ID, as sign-in's
/// adoption does. <c>id-taken</c> makes the phone re-key; <c>already-has-family</c> means they were invited back.
/// </summary>
public sealed class CreateHousehold(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken) =>
        store.RunLockedAsync([], async tx =>
        {
            var active = await tx.ActiveMembershipsOfUserAsync(user, cancellationToken);
            // A retry after a lost answer: this family already exists with them as its only active membership.
            if (active is [{ Role: Role.Owner } only] && only.HouseholdId == household)
                return Result.Success;
            if (active.Count > 0)
                return Result.Failure(HouseholdErrors.AlreadyHasFamily);
            return await tx.TryCreateHouseholdAsync(household, user, time.GetUtcNow(), cancellationToken)
                ? Result.Success
                : Result.Failure(HouseholdErrors.IdTaken);
        }, cancellationToken);
}
