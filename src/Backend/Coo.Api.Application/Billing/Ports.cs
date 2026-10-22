using Coo.Domain.Common;

namespace Coo.Api.Application.Billing;

public interface IBillingStore
{
    Task SaveSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken);
    Task<Subscription?> FindSubscriptionAsync(string purchaseToken, CancellationToken cancellationToken);
    Task<bool> IsHouseholdPremiumAsync(HouseholdId householdId, CancellationToken cancellationToken);
}

public sealed record Subscription(
    string PurchaseToken,
    HouseholdId HouseholdId,
    Guid UserId,
    string ProductId,
    SubscriptionStatus Status,
    DateTime ExpiryDate,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public enum SubscriptionStatus
{
    Active,
    GracePeriod,
    Paused,
    Canceled,
    Expired
}

public interface IGooglePlayValidator
{
    Task<GooglePlayPurchaseInfo?> VerifyPurchaseAsync(string productId, string purchaseToken, CancellationToken cancellationToken);
}

public sealed record GooglePlayPurchaseInfo(
    string PurchaseToken,
    string ProductId,
    DateTime ExpiryDate,
    SubscriptionStatus Status,
    bool AutoRenewing,
    string? ObfuscatedAccountId = null
);
