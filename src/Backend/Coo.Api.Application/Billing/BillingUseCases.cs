using Coo.Domain.Common;

namespace Coo.Api.Application.Billing;

public sealed class VerifyReceipt(IBillingStore store, IGooglePlayValidator validator, TimeProvider time)
{
    public async Task<bool> ExecuteAsync(HouseholdId householdId, Guid userId, string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        var purchase = await validator.VerifyPurchaseAsync(productId, purchaseToken, cancellationToken);
        if (purchase == null)
            return false;

        var subscription = new Subscription(
            purchase.PurchaseToken,
            householdId,
            userId,
            purchase.ProductId,
            purchase.Status,
            purchase.ExpiryDate,
            time.GetUtcNow().UtcDateTime,
            time.GetUtcNow().UtcDateTime
        );

        await store.SaveSubscriptionAsync(subscription, cancellationToken);
        return true;
    }
}

public sealed class HandleRTDN(IBillingStore store, IGooglePlayValidator validator, TimeProvider time)
{
    public async Task ExecuteAsync(string purchaseToken, CancellationToken cancellationToken)
    {
        var existing = await store.FindSubscriptionAsync(purchaseToken, cancellationToken);
        if (existing == null) return;

        var purchase = await validator.VerifyPurchaseAsync(existing.ProductId, purchaseToken, cancellationToken);
        if (purchase == null) return;

        var updated = existing with
        {
            Status = purchase.Status,
            ExpiryDate = purchase.ExpiryDate,
            UpdatedAt = time.GetUtcNow().UtcDateTime
        };

        await store.SaveSubscriptionAsync(updated, cancellationToken);
    }
}

public sealed class GetEntitlementStatus(IBillingStore store)
{
    public Task<bool> ExecuteAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        return store.IsHouseholdPremiumAsync(householdId, cancellationToken);
    }
}
