using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

public sealed record CreatedInvitation(InvitationId Id, InvitationCode Code, DateTimeOffset ExpiresAt);

/// <summary>
/// <c>POST /api/households/{id}/invitations</c> (spec §3). An Owner or Parent; Parent invitations only (R2), so a
/// Caregiver invitation later is an Application change. At most 5 open invitations per family, checked under the lock.
/// </summary>
public sealed class CreateInvitation(IHouseholdMembershipStore store, TimeProvider time, Func<int, int> nextIndex)
{
    public const int MaxOpenPerFamily = 5;

    public Task<Result<CreatedInvitation>> ExecuteAsync(UserId user, HouseholdId household, string? role, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result<CreatedInvitation>.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { CanInvite: true } || me.IsRevoked)
                return Result<CreatedInvitation>.Failure(HouseholdErrors.Forbidden);
            // After the membership check: a non-member always gets 403, whatever role they ask for (spec §10.1).
            if (role != AuthRoles.Parent)
                return Result<CreatedInvitation>.Failure(HouseholdErrors.InvitationRoleUnsupported);

            var now = time.GetUtcNow();
            if (await tx.CountOpenInvitationsAsync(household, now, cancellationToken) >= MaxOpenPerFamily)
                return Result<CreatedInvitation>.Failure(HouseholdErrors.InvitationTooMany);

            var code = InvitationCode.FromRandom(nextIndex);
            var invitation = Invitation.Create(new InvitationId(Guid.CreateVersion7(now)), household, Role.Parent, user, now);
            await tx.AddInvitationAsync(invitation, AuthSecrets.Hash(code.Value), cancellationToken);
            return Result<CreatedInvitation>.Ok(new CreatedInvitation(invitation.Id, code, invitation.ExpiresAt));
        }, cancellationToken);
}

/// <summary><c>DELETE /api/households/{id}/invitations/{invitationId}</c>: already withdrawn, used, expired or unknown is a success.</summary>
public sealed class WithdrawInvitation(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, InvitationId invitationId, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { IsParent: true } member || me.IsRevoked)
                return Result.Failure(HouseholdErrors.Forbidden);
            var invitation = await tx.FindInvitationForUpdateAsync(household, invitationId, cancellationToken);
            if (invitation is null || !member.CanWithdraw(invitation))
                return Result.Success;
            invitation.Withdraw(time.GetUtcNow());
            await tx.SaveInvitationAsync(invitation, cancellationToken);
            return Result.Success;
        }, cancellationToken);
}
