using Coo.Domain.Common;

namespace Coo.Mobile.Application.Billing;

/// <summary>The ways to buy Premium: the subscription's two base plans, or once for good.</summary>
public enum PremiumPlan
{
    Monthly,
    Yearly,
    Lifetime,
}

/// <summary>A plan as Google Play prices it for this account: the localized price, and whether a free trial comes first.</summary>
public sealed record PremiumOffer(PremiumPlan Plan, string Price, long PriceMicros, bool HasFreeTrial);

public interface IBillingService
{
    /// <summary>The plans Play offers this account, cheapest period first; empty when Play cannot be reached.</summary>
    Task<IReadOnlyList<PremiumOffer>> GetOffersAsync(CancellationToken cancellationToken);
    Task<bool> PurchasePremiumAsync(HouseholdId householdId, PremiumPlan plan, CancellationToken cancellationToken);
    Task<bool> CheckEntitlementAsync(HouseholdId householdId, CancellationToken cancellationToken);
    bool IsPremiumCached { get; }

    /// <summary>True when Premium on this phone was bought once (lifetime): there is no subscription to manage.</summary>
    bool IsLifetimeCached { get; }
    event Action? EntitlementChanged;
    void SetPremiumForDebug(bool isPremium);
}
