using Coo.Domain.Common;

namespace Coo.Api.Application.Billing;

public interface IBillingStore
{
    Task SaveSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken);
    Task<Subscription?> FindSubscriptionAsync(string purchaseToken, CancellationToken cancellationToken);
    Task<bool> IsHouseholdPremiumAsync(HouseholdId householdId, CancellationToken cancellationToken);

    /// <summary>
    /// The purchases to ask Google about again (<see cref="RecheckPurchases"/>), soonest expiry first. Not expired more than
    /// a week ago, and not checked in the last hour; then due when it runs out within a day (or ran out lately), is less
    /// than a week old, is on hold or pending, or was last checked over a day ago.
    /// </summary>
    Task<IReadOnlyList<Subscription>> ListDueForRecheckAsync(DateTime now, int limit, CancellationToken cancellationToken);
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

/// <summary>
/// A purchase's state as Coo keeps it. <see cref="Active"/>, <see cref="GracePeriod"/> and <see cref="Canceled"/> grant
/// Premium until the expiry: a cancelled subscription has been paid to the end of its period (ADR 0004 §3).
/// <see cref="Paused"/> also stands for on hold and a pending payment; neither grants.
/// </summary>
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
    /// <summary>The purchase as Google has it now; null when Google does not know the token.</summary>
    /// <exception cref="PlayUnavailableException">Google could not be asked (not configured, unreachable, refused): nothing was decided.</exception>
    Task<GooglePlayPurchaseInfo?> VerifyPurchaseAsync(string productId, string purchaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledges the purchase with Google, which otherwise refunds it after three days. Called only once the purchase is
    /// recorded (grant, then acknowledge: ADR 0004 §4).
    /// </summary>
    /// <exception cref="PlayUnavailableException">Google could not be asked; the app or the recheck tries again.</exception>
    Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken cancellationToken);
}

/// <summary>Google Play could not be asked about a purchase. Not an answer: the caller retries later.</summary>
public sealed class PlayUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GooglePlayPurchaseInfo(
    string PurchaseToken,
    string ProductId,
    DateTime ExpiryDate,
    SubscriptionStatus Status,
    bool AutoRenewing,
    string? ObfuscatedAccountId = null,
    bool NeedsAcknowledgement = false
);
