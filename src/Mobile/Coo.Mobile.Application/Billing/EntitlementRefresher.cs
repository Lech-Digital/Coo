using Coo.Domain.Common;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Billing;

/// <summary>
/// Asks again whether the family on screen has Premium: at start-up, when Coo comes back to the foreground (at most every
/// half hour), and at once when the family on screen is another one than last time. Without this a trial that ended,
/// a subscription a partner cancelled or a family switch showed the old answer until the next cold start.
/// </summary>
/// <remarks>Signed out, there is no Premium: it is the family's, bought by a signed-in parent (owner's test, 2026-11-03).</remarks>
public sealed class EntitlementRefresher(IBillingService billing, IHouseholdStore households, Auth.Account account, TimeProvider time)
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(30);

    private int _running;
    private HouseholdId? _lastHousehold;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <returns>Whether the family has Premium; null when there is no family yet or a check is already running.</returns>
    public async Task<bool?> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return null;
        try
        {
            if (account.State is Auth.SignedOut)
            {
                billing.Forget();
                _lastHousehold = null;
                return false;
            }

            var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
            if (household is null)
                return null;

            var now = time.GetUtcNow();
            if (!force && household.Id == _lastHousehold && now - _lastCheck < MinimumInterval)
                return billing.IsPremiumCached;

            (_lastHousehold, _lastCheck) = (household.Id, now);
            return await billing.CheckEntitlementAsync(household.Id, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
