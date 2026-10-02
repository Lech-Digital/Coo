using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

internal static class AccountStoreExtensions
{
    /// <summary>
    /// One household per user in this increment (Spec 010 §3.2): the user's earliest membership, or null when they have none.
    /// The one place this rule lives, for sign-in and account deletion.
    /// </summary>
    public static async Task<Membership?> FindEarliestMembershipAsync(
        this IAccountStore accounts, UserId user, CancellationToken cancellationToken) =>
        (await accounts.ListMembershipsOfUserAsync(user, cancellationToken)).OrderBy(m => m.JoinedAt).FirstOrDefault();
}
