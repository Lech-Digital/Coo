using Coo.Domain.Common;

namespace Coo.Mobile.Application.Billing;

/// <summary>The ways to buy Premium: the subscription's two base plans, or once for good.</summary>
public enum PremiumPlan
{
    Monthly,
    Yearly,
    Lifetime,
}

/// <summary>How a purchase ended.</summary>
public enum PurchaseOutcome
{
    /// <summary>Paid: Premium now.</summary>
    Purchased,

    /// <summary>Accepted by Play, payment still to come (a slow card, cash at a shop): Premium once it is paid.</summary>
    Pending,

    /// <summary>The person closed Play's sheet. Nothing to say.</summary>
    Cancelled,

    /// <summary>Play refused or could not be reached.</summary>
    Failed,
}

/// <summary>A plan as Google Play prices it for this account: the localized price, and whether a free trial comes first.</summary>
public sealed record PremiumOffer(PremiumPlan Plan, string Price, long PriceMicros, bool HasFreeTrial);

public interface IBillingService
{
    /// <summary>The plans Play offers this account, cheapest period first; empty when Play cannot be reached.</summary>
    Task<IReadOnlyList<PremiumOffer>> GetOffersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Buys <paramref name="plan"/> for <paramref name="householdId"/>'s family. The purchase is reported to the server first
    /// and acknowledged with Play after, so a failed report never leaves a charge without Premium (ADR 0004 §4).
    /// </summary>
    Task<PurchaseOutcome> PurchasePremiumAsync(HouseholdId householdId, PremiumPlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the family has Premium: this Google account's own purchase (reported to the server and acknowledged again
    /// if that never happened), or else the server's answer for the family. Offline, what this phone last heard.
    /// </summary>
    Task<bool> CheckEntitlementAsync(HouseholdId householdId, CancellationToken cancellationToken);

    /// <summary>Premium as this phone last heard it; false once that is more than <c>OfflineGrace</c> old and unconfirmed.</summary>
    bool IsPremiumCached { get; }

    /// <summary>True when Premium on this phone was bought once (lifetime): there is no subscription to manage.</summary>
    bool IsLifetimeCached { get; }

    /// <summary>True when this phone's Google account holds the purchase, so it can manage the subscription in Play.</summary>
    bool IsPayerCached { get; }

    event Action? EntitlementChanged;

    /// <summary>Signed out: what this phone remembered about Premium goes; the next sign-in asks again.</summary>
    void Forget();

    void SetPremiumForDebug(bool isPremium);
}
