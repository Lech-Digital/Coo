using Coo.Api.Application.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Application.Billing;

/// <summary>The Play products Coo sells. A receipt for anything else is refused.</summary>
public static class Products
{
    public const string Premium = "premium_subscription";

    public static bool IsKnown(string productId) => productId == Premium;
}

public enum VerifyReceiptOutcome
{
    Verified,

    /// <summary>The caller is not a member of the household (or was removed from it).</summary>
    NotAMember,

    /// <summary>Not one of <see cref="Products"/>.</summary>
    UnknownProduct,

    /// <summary>Google does not know the token, or it is for another product.</summary>
    Invalid,

    /// <summary>The purchase was made for, or is already held by, another household.</summary>
    OtherHousehold,
}

public sealed class VerifyReceipt(IBillingStore store, IGooglePlayValidator validator, IMembershipStore memberships, TimeProvider time)
{
    public async Task<VerifyReceiptOutcome> ExecuteAsync(HouseholdId householdId, UserId user, string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindAsync(householdId, user, cancellationToken);
        if (membership.Membership is null || membership.IsRevoked)
            return VerifyReceiptOutcome.NotAMember;
        if (!Products.IsKnown(productId))
            return VerifyReceiptOutcome.UnknownProduct;

        var purchase = await validator.VerifyPurchaseAsync(productId, purchaseToken, cancellationToken);
        if (purchase is null || purchase.ProductId != productId)
            return VerifyReceiptOutcome.Invalid;

        // The app buys with the household's ID as Play's obfuscated account ID, so a token cannot be replayed for
        // another family; nor can a token already held by one family move to another.
        if (purchase.ObfuscatedAccountId is { } boughtFor && boughtFor != householdId.Value.ToString())
            return VerifyReceiptOutcome.OtherHousehold;
        var existing = await store.FindSubscriptionAsync(purchaseToken, cancellationToken);
        if (existing is not null && existing.HouseholdId != householdId)
            return VerifyReceiptOutcome.OtherHousehold;

        var now = time.GetUtcNow().UtcDateTime;
        await store.SaveSubscriptionAsync(
            new Subscription(
                purchase.PurchaseToken,
                householdId,
                user.Value,
                purchase.ProductId,
                purchase.Status,
                purchase.ExpiryDate,
                existing?.CreatedAt ?? now,
                now),
            cancellationToken);
        return VerifyReceiptOutcome.Verified;
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

/// <summary>Whether a household has Premium. Only its members may ask.</summary>
public sealed class GetEntitlementStatus(IBillingStore store, IMembershipStore memberships)
{
    /// <returns>Null when the caller is not a member.</returns>
    public async Task<bool?> ExecuteAsync(HouseholdId householdId, UserId user, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindAsync(householdId, user, cancellationToken);
        if (membership.Membership is null || membership.IsRevoked)
            return null;
        return await store.IsHouseholdPremiumAsync(householdId, cancellationToken);
    }
}
