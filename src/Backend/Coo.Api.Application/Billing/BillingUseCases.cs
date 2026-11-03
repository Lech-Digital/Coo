using Coo.Api.Application.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Application.Billing;

/// <summary>The Play products Coo sells. A receipt for anything else is refused.</summary>
public static class Products
{
    /// <summary>The subscription, with a monthly and a yearly base plan.</summary>
    public const string Premium = "premium_subscription";

    /// <summary>Premium bought once, for good: a one-time product, not a subscription.</summary>
    public const string Lifetime = "premium_lifetime";

    /// <summary>The expiry recorded for a purchase that never runs out.</summary>
    public static readonly DateTime NeverExpires = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsKnown(string productId) => productId is Premium or Lifetime;

    public static bool IsOneTime(string productId) => productId == Lifetime;

    /// <summary>
    /// Whether a purchase in <paramref name="status"/> that runs until <paramref name="expiry"/> gives Premium at
    /// <paramref name="now"/>. Cancelled counts: the family keeps what was paid for until the period ends (ADR 0004 §3).
    /// </summary>
    public static bool Grants(SubscriptionStatus status, DateTime expiry, DateTime now) =>
        (status is SubscriptionStatus.Active or SubscriptionStatus.GracePeriod or SubscriptionStatus.Canceled) && expiry > now;
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

    /// <summary>Google could not be asked: nothing was decided, and the app tries again later.</summary>
    Unavailable,
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

        GooglePlayPurchaseInfo? purchase;
        try
        {
            purchase = await validator.VerifyPurchaseAsync(productId, purchaseToken, cancellationToken);
        }
        catch (PlayUnavailableException)
        {
            return VerifyReceiptOutcome.Unavailable;
        }

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

        // Grant, then acknowledge (ADR 0004 §4): recorded first, so a failure here never leaves a charge without Premium.
        // If Google cannot be reached now, the app's next check or the hourly recheck acknowledges it.
        await PlayPurchases.TryAcknowledgeAsync(validator, purchase, cancellationToken);
        return VerifyReceiptOutcome.Verified;
    }
}

/// <summary>Reading a recorded purchase again from Google and keeping what Google says now.</summary>
internal static class PlayPurchases
{
    /// <returns>False when Google could not be asked.</returns>
    public static async Task<bool> RefreshAsync(
        IBillingStore store, IGooglePlayValidator validator, Subscription existing, DateTime now, CancellationToken cancellationToken)
    {
        GooglePlayPurchaseInfo? purchase;
        try
        {
            purchase = await validator.VerifyPurchaseAsync(existing.ProductId, existing.PurchaseToken, cancellationToken);
        }
        catch (PlayUnavailableException)
        {
            return false;
        }

        // Google no longer knows the token (a long-expired purchase): it grants nothing.
        var updated = purchase is null
            ? existing with { Status = SubscriptionStatus.Expired, ExpiryDate = existing.ExpiryDate < now ? existing.ExpiryDate : now, UpdatedAt = now }
            : existing with { Status = purchase.Status, ExpiryDate = purchase.ExpiryDate, UpdatedAt = now };
        await store.SaveSubscriptionAsync(updated, cancellationToken);
        if (purchase is not null)
            await TryAcknowledgeAsync(validator, purchase, cancellationToken);
        return true;
    }

    /// <summary>Acknowledges a purchase that grants and is not acknowledged yet; a failure is left for the next try.</summary>
    public static async Task TryAcknowledgeAsync(IGooglePlayValidator validator, GooglePlayPurchaseInfo purchase, CancellationToken cancellationToken)
    {
        if (!purchase.NeedsAcknowledgement || purchase.Status is not (SubscriptionStatus.Active or SubscriptionStatus.GracePeriod or SubscriptionStatus.Canceled))
            return;
        try
        {
            await validator.AcknowledgeAsync(purchase.ProductId, purchase.PurchaseToken, cancellationToken);
        }
        catch (PlayUnavailableException)
        {
            // Retried by the app's next check and by RecheckPurchases.
        }
    }
}

/// <summary>
/// A Real-Time Developer Notification names a purchase that changed. The message is only a hint: the purchase is read
/// again from Google, and what Google says now is kept (ADR 0004 §6).
/// </summary>
public sealed class HandleRTDN(IBillingStore store, IGooglePlayValidator validator, TimeProvider time)
{
    /// <returns>False when Google could not be asked: the webhook answers 503 and Pub/Sub delivers it again.</returns>
    public async Task<bool> ExecuteAsync(string purchaseToken, CancellationToken cancellationToken)
    {
        var existing = await store.FindSubscriptionAsync(purchaseToken, cancellationToken);
        // Not recorded yet: the app reports it itself, and that report reads it from Google.
        if (existing == null)
            return true;
        return await PlayPurchases.RefreshAsync(store, validator, existing, time.GetUtcNow().UtcDateTime, cancellationToken);
    }
}

/// <summary>
/// The safety net under the notifications (ADR 0004 §6): every purchase that grants, or ran out in the last week, is read
/// again from Google when it was last checked over a day ago, or over an hour ago when it is about to run out, ran out
/// lately or is less than a week old. Renewals, refunds, cancellations and missed acknowledgements are caught even when
/// no notification arrives. Coo stays correct without Pub/Sub, only slower.
/// </summary>
public sealed class RecheckPurchases(IBillingStore store, IGooglePlayValidator validator, TimeProvider time)
{
    /// <summary>At most this many Google lookups per run, so a backlog spreads over several runs.</summary>
    public const int BatchSize = 200;

    /// <returns>How many purchases were read again, and how many could not be (Google unavailable).</returns>
    public async Task<(int Checked, int Failed)> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await store.ListDueForRecheckAsync(now, BatchSize, cancellationToken);
        var (done, failed) = (0, 0);
        foreach (var purchase in due)
        {
            if (await PlayPurchases.RefreshAsync(store, validator, purchase, now, cancellationToken))
                done++;
            else
                failed++;
        }

        return (done, failed);
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
