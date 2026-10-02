using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

public sealed record AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices);

/// <summary><c>GET /api/me</c>: the user, their household and role, and their active device sessions.</summary>
public sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions)
{
    public async Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(user, cancellationToken);
        if (account is null)
            return null;
        var membership = await accounts.FindEarliestMembershipAsync(user, cancellationToken);
        var devices = (await sessions.ListForUserAsync(user, cancellationToken))
            .Where(s => !s.IsRevoked)
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        return new AccountOverview(account, membership, devices);
    }
}

/// <summary>
/// <c>DELETE /api/account</c> (Spec 010 §5, ADR 0003 §8). For each household the user belongs to, the succession rule
/// decides first: ownership passes to the earliest parent, or, when they were the last parent, the household and its
/// care data go. Then the user, their logins, sessions and memberships go, all in one transaction.
/// </summary>
public sealed class DeleteAccount(IAccountStore accounts)
{
    public async Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
            return Result.Failure(AuthErrors.ConfirmationRequired);

        var deletions = new List<HouseholdId>();
        var transfers = new List<OwnershipTransfer>();
        foreach (var membership in await accounts.ListMembershipsOfUserAsync(user, cancellationToken))
        {
            var members = await accounts.ListMembersAsync(membership.HouseholdId, cancellationToken);
            switch (HouseholdSuccession.WhenLeaving(members, user))
            {
                case HouseholdDeleted:
                    deletions.Add(membership.HouseholdId);
                    break;
                case OwnershipPasses passes:
                    transfers.Add(new OwnershipTransfer(membership.HouseholdId, passes.NewOwner));
                    break;
            }
        }

        await accounts.DeleteAccountAsync(user, new AccountDeletionPlan(deletions, transfers), cancellationToken);
        return Result.Success;
    }
}
