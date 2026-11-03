using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

/// <summary>
/// A Viewer (a grandparent following along) may not write to that family's log; everyone else may, and so may anyone
/// signed out (the phone's own family). Read from the memberships this phone last heard: the server refuses the
/// writes of a Viewer anyway (it checks the role on every push).
/// </summary>
public sealed class FamilyLogAccess(Account account, IFamilyStore store) : ILogAccess
{
    public async Task<bool> CanLogAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        if (account.State is not SignedIn { Profile: var profile })
            return true;
        var memberships = await store.ListAsync(profile.UserId, cancellationToken);
        return memberships?.FirstOrDefault(m => m.Household == household) is not { Role: Role.Viewer };
    }

    public async Task<bool> CanPlanAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        if (account.State is not SignedIn { Profile: var profile })
            return true;
        var memberships = await store.ListAsync(profile.UserId, cancellationToken);
        return memberships?.FirstOrDefault(m => m.Household == household) is not { Role: Role.Caregiver or Role.Viewer };
    }
}
