using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

/// <param name="Memberships">The active ones, earliest joined first (Spec 011b §10.1); <see cref="Membership"/> is the first.</param>
public sealed record AccountOverview(
    UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices, ConsentRecord? Consent = null,
    IReadOnlyList<Membership>? Memberships = null);

/// <summary>
/// <c>GET /api/me</c>: the user, their active families and roles, their active device sessions, and their consent to the
/// current wording (Spec 011 §3). <c>household</c> stays the earliest active family, for 0.11.0 phones.
/// </summary>
public sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions, IConsentStore consents)
{
    public async Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(user, cancellationToken);
        if (account is null)
            return null;
        var memberships = (await accounts.ListMembershipsOfUserAsync(user, cancellationToken)).OrderBy(m => m.JoinedAt).ToList();
        var devices = (await sessions.ListForUserAsync(user, cancellationToken))
            .Where(s => !s.IsRevoked)
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        var consent = await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken);
        return new AccountOverview(account, memberships.FirstOrDefault(), devices, consent, memberships);
    }
}

/// <summary>
/// <c>DELETE /api/account</c> (Spec 010 §5, ADR 0003 §8; Spec 011b §10.4). Every family the user is an active member of is
/// locked first; the succession rule then decides each from the members read inside the transaction, so a concurrent
/// accept either counts (it committed first) or finds the family gone. A promotion must change exactly one row; anything
/// else throws and rolls the whole deletion back.
/// </summary>
public sealed class DeleteAccount(IHouseholdMembershipStore store)
{
    public async Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
            return Result.Failure(AuthErrors.ConfirmationRequired);

        var families = (await store.ListActiveMembershipsOfUserAsync(user, cancellationToken)).Select(m => m.HouseholdId).ToList();
        return await store.RunLockedAsync(families, async tx =>
        {
            var deletions = new List<HouseholdId>();
            var transfers = new List<(HouseholdId Household, UserId NewOwner)>();
            foreach (var membership in await tx.ActiveMembershipsOfUserAsync(user, cancellationToken))
            {
                // A family gained after the read that chose the locks would be decided unlocked: refuse, the phone retries from fresh reads.
                if (!tx.IsLocked(membership.HouseholdId))
                    throw new InvalidOperationException($"{membership.HouseholdId} became an active family of {user} after the locks were taken.");
                var members = await tx.ActiveMembersAsync(membership.HouseholdId, cancellationToken);
                switch (HouseholdSuccession.WhenLeaving(members, user))
                {
                    case HouseholdDeleted:
                        deletions.Add(membership.HouseholdId);
                        break;
                    case OwnershipPasses passes:
                        transfers.Add((membership.HouseholdId, passes.NewOwner));
                        break;
                }
            }

            // The leaver's rows first, so a promoted parent never meets a second Owner (ix_memberships_one_owner).
            await tx.DeleteMembershipsOfUserAsync(user, cancellationToken);
            foreach (var (household, newOwner) in transfers)
            {
                if (await tx.PromoteToOwnerAsync(household, newOwner, cancellationToken) != 1)
                    throw new InvalidOperationException($"Promoting {newOwner} in {household} did not change exactly one row.");
            }

            foreach (var household in deletions)
                await tx.DeleteHouseholdAsync(household, cancellationToken);
            await tx.DeleteUserAsync(user, cancellationToken);
            return Result.Success;
        }, cancellationToken);
    }
}
