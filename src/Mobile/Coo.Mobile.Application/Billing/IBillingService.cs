using Coo.Domain.Common;

namespace Coo.Mobile.Application.Billing;

public interface IBillingService
{
    Task<string?> GetPremiumPriceAsync(CancellationToken cancellationToken);
    Task<bool> PurchasePremiumAsync(HouseholdId householdId, CancellationToken cancellationToken);
    Task<bool> CheckEntitlementAsync(HouseholdId householdId, CancellationToken cancellationToken);
    bool IsPremiumCached { get; }
    event Action? EntitlementChanged;
    void SetPremiumForDebug(bool isPremium);
}
