using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

internal static class AccountStoreExtensions
{
    /// <summary>
    /// The user's earliest <b>active</b> membership, or null when they have none (Spec 011b §10.3: a removed user must not
    /// sign in to the family that removed them). The one place this rule lives, for sign-in.
    /// </summary>
    public static async Task<Membership?> FindEarliestMembershipAsync(
        this IAccountStore accounts, UserId user, CancellationToken cancellationToken) =>
        (await accounts.ListMembershipsOfUserAsync(user, cancellationToken)).OrderBy(m => m.JoinedAt).FirstOrDefault();
}
