using Coo.Api.Application.Auth;
using Coo.Api.Application.Sync;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
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

public sealed record InvitationPreview(
    HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

/// <summary>
/// <c>POST /api/invitations/preview</c> (spec §5.1 step 2). One answer, <c>invitation.invalid</c>, for unknown, expired,
/// used, withdrawn and malformed codes. A code this caller already used, while still a member, previews as already-member.
/// </summary>
public sealed class PreviewInvitation(IInvitationStore invitations, IMembershipStore memberships, TimeProvider time)
{
    public async Task<Result<InvitationPreview>> ExecuteAsync(UserId user, string? code, CancellationToken cancellationToken)
    {
        var parsed = InvitationCode.Parse(code);
        if (parsed.IsFailure || await invitations.FindByHashAsync(AuthSecrets.Hash(parsed.Value.Value), cancellationToken) is not { } found)
            return Result<InvitationPreview>.Failure(HouseholdErrors.InvitationInvalid);

        var invitation = found.Invitation;
        var me = await memberships.FindAsync(invitation.HouseholdId, user, cancellationToken);
        var active = me.Membership is not null && !me.IsRevoked;
        if (!invitation.IsUsable(time.GetUtcNow()) && !(active && invitation.WasAcceptedBy(user)))
            return Result<InvitationPreview>.Failure(HouseholdErrors.InvitationInvalid);

        var babies = await invitations.ListBabyNamesAsync(invitation.HouseholdId, cancellationToken);
        return new InvitationPreview(invitation.HouseholdId, found.InviterName, babies, invitation.Role, invitation.ExpiresAt, active);
    }
}

public enum AcceptOutcome
{
    Joined,
    Rejoined,
    AlreadyMember,
}

public sealed record AcceptResult(HouseholdId Household, Role Role, AcceptOutcome Outcome);

/// <summary>
/// <c>POST /api/invitations/accept</c> (spec §10.4): find by hash, lock the family, re-read the invitation, then decide.
/// No membership without consent (R4): <c>sync.consent-required</c> changes nothing.
/// </summary>
public sealed class AcceptInvitation(IInvitationStore invitations, IHouseholdMembershipStore store, IConsentStore consents, TimeProvider time)
{
    public async Task<Result<AcceptResult>> ExecuteAsync(UserId user, string? code, CancellationToken cancellationToken)
    {
        var parsed = InvitationCode.Parse(code);
        if (parsed.IsFailure)
            return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
        var hash = AuthSecrets.Hash(parsed.Value.Value);
        if (await invitations.FindByHashAsync(hash, cancellationToken) is not { } found)
            return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
        var household = found.Invitation.HouseholdId;
        var consented = await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken) is not null;

        return await store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household) || await tx.FindInvitationForUpdateAsync(hash, cancellationToken) is not { } invitation)
                return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);

            // The re-read is the truth: a row that is no longer this family's must not join the user to the family that is locked.
            if (invitation.HouseholdId != household)
                return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);

            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            var active = me.Membership is not null && !me.IsRevoked;
            if (active && invitation.WasAcceptedBy(user))
                return Result<AcceptResult>.Ok(new AcceptResult(household, me.Membership!.Role, AcceptOutcome.Joined));

            var now = time.GetUtcNow();
            if (!invitation.IsUsable(now))
                return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
            if (!consented)
                return Result<AcceptResult>.Failure(SyncErrors.ConsentRequired);
            if (active)
                return Result<AcceptResult>.Ok(new AcceptResult(household, me.Membership!.Role, AcceptOutcome.AlreadyMember));

            AcceptOutcome outcome;
            if (me.IsRevoked)
            {
                await tx.ReactivateMembershipAsync(household, user, invitation.Role, now, cancellationToken);
                outcome = AcceptOutcome.Rejoined;
            }
            else
            {
                await tx.AddMembershipAsync(Membership.Create(household, user, invitation.Role, now), cancellationToken);
                outcome = AcceptOutcome.Joined;
            }

            invitation.Accept(user, now);
            await tx.SaveInvitationAsync(invitation, cancellationToken);
            return Result<AcceptResult>.Ok(new AcceptResult(household, invitation.Role, outcome));
        }, cancellationToken);
    }
}
