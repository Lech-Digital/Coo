using Coo.Domain.Common;
using Coo.Mobile.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Plugin.InAppBilling;

namespace Coo.Mobile.Platform.Billing;

/// <summary>
/// Premium through Google Play (Spec 012): a subscription with a monthly and a yearly base plan, or a one-time lifetime
/// product. A purchase is made with the household's ID as Play's obfuscated account ID and handed to the server, which
/// verifies it and gives Premium to the whole family; this phone also remembers it, so Premium shows offline.
/// </summary>
public class BillingService(IServiceProvider? services = null) : IBillingService
{
    public const string SubscriptionId = "premium_subscription";
    public const string LifetimeId = "premium_lifetime";
    private const string PremiumPreferenceKey = "billing.is_premium";
    private const string LifetimePreferenceKey = "billing.is_lifetime";

    public bool IsPremiumCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(PremiumPreferenceKey, false);

    public bool IsLifetimeCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(LifetimePreferenceKey, false);

    public event Action? EntitlementChanged;

    // Resolved late: the billing API is registered by Infrastructure, after Platform.
    private IBillingApi? Api => services?.GetService<IBillingApi>();

    public void SetPremiumForDebug(bool isPremium) => Remember(isPremium, false);

    private static string BasePlanOf(PremiumPlan plan) => plan == PremiumPlan.Yearly ? "yearly" : "monthly";

    public async Task<IReadOnlyList<PremiumOffer>> GetOffersAsync(CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            if (!await billing.ConnectAsync(true))
                return [];
            var subscription = (await billing.GetProductInfoAsync(ItemType.Subscription, [SubscriptionId]))?.FirstOrDefault();
            var lifetime = (await billing.GetProductInfoAsync(ItemType.InAppPurchase, [LifetimeId]))?.FirstOrDefault();
#pragma warning restore CA2016

            var offers = new List<PremiumOffer>();
            foreach (var plan in new[] { PremiumPlan.Monthly, PremiumPlan.Yearly })
            {
                var details = subscription?.AndroidExtras?.SubscriptionOfferDetails?.Where(d => d.BasePlanId == BasePlanOf(plan)).ToList() ?? [];
                // The base plan's own entry has no offer ID; its last pricing phase is the renewing price.
                var basePrice = details.FirstOrDefault(d => string.IsNullOrEmpty(d.OfferId))?.PricingPhases?.LastOrDefault();
                if (basePrice is null)
                    continue;
                var trial = details.Any(d => !string.IsNullOrEmpty(d.OfferId) && d.PricingPhases?.Any(p => p.PriceAmountMicros == 0) == true);
                offers.Add(new PremiumOffer(plan, basePrice.FormattedPrice, basePrice.PriceAmountMicros, trial));
            }

            if (lifetime is not null)
                offers.Add(new PremiumOffer(PremiumPlan.Lifetime, lifetime.LocalizedPrice, lifetime.MicrosPrice, false));
            return offers;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get premium offers: {ex}");
            return [];
        }
        finally
        {
            await billing.DisconnectAsync(CancellationToken.None);
        }
    }

    public async Task<bool> PurchasePremiumAsync(HouseholdId householdId, PremiumPlan plan, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            if (!await billing.ConnectAsync(true))
                return false;

            var lifetime = plan == PremiumPlan.Lifetime;
            var productId = lifetime ? LifetimeId : SubscriptionId;
            string? offerToken = null;
            if (!lifetime)
            {
                // Play picks the first offer of a product unless told: name the chosen base plan, with its free trial
                // when this account may still have one (Play leaves the trial out of the list otherwise).
                var subscription = (await billing.GetProductInfoAsync(ItemType.Subscription, [SubscriptionId]))?.FirstOrDefault();
                var details = subscription?.AndroidExtras?.SubscriptionOfferDetails?.Where(d => d.BasePlanId == BasePlanOf(plan)).ToList() ?? [];
                var chosen = details.FirstOrDefault(d => !string.IsNullOrEmpty(d.OfferId) && d.PricingPhases?.Any(p => p.PriceAmountMicros == 0) == true)
                    ?? details.FirstOrDefault(d => string.IsNullOrEmpty(d.OfferId));
                if (chosen is null)
                    return false;
                offerToken = chosen.OfferToken;
            }

            var purchase = await billing.PurchaseAsync(productId, lifetime ? ItemType.InAppPurchase : ItemType.Subscription, householdId.Value.ToString(), null, offerToken);
            if (purchase is not { State: PurchaseState.Purchased })
                return false;

            // Unacknowledged purchases are refunded by Play after three days. Lifetime is acknowledged, never consumed:
            // consuming it would let it be bought again and lose it from this account's purchases.
            if (!purchase.IsAcknowledged.GetValueOrDefault())
                await billing.FinalizePurchaseAsync([purchase.PurchaseToken]);
#pragma warning restore CA2016

            Remember(true, lifetime || IsLifetimeCached);
            if (Api is { } api)
                await api.VerifyReceiptAsync(householdId.Value, productId, purchase.PurchaseToken, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to purchase premium: {ex}");
            return false;
        }
        finally
        {
            await billing.DisconnectAsync(CancellationToken.None);
        }
    }

    public async Task<bool> CheckEntitlementAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            var connected = await billing.ConnectAsync(true);
            var lifetime = connected
                ? (await billing.GetPurchasesAsync(ItemType.InAppPurchase))?.FirstOrDefault(p => p.ProductId == LifetimeId && p.State == PurchaseState.Purchased)
                : null;
            var subscription = connected && lifetime is null
                ? (await billing.GetPurchasesAsync(ItemType.Subscription))?.FirstOrDefault(p => p.ProductId == SubscriptionId && p.State == PurchaseState.Purchased)
                : null;
#pragma warning restore CA2016
            if ((lifetime ?? subscription) is { } own)
            {
                Remember(true, lifetime is not null);
                // Again on every check: a purchase whose report did not reach the server (offline) gets there now,
                // and the server keeps the expiry up to date. It refuses a purchase made for another family.
                if (Api is { } api)
                    await api.VerifyReceiptAsync(householdId.Value, own.ProductId, own.PurchaseToken, cancellationToken);
                return true;
            }

            // Not bought on this Google account: another member of the family may have bought it.
            // Unknown (offline, signed out) keeps what this phone remembers.
            if (Api is { } server && await server.CheckEntitlementAsync(householdId.Value, cancellationToken) is { } isPremium)
                Remember(isPremium, false);

            return IsPremiumCached;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to check entitlement: {ex}");
            return IsPremiumCached;
        }
        finally
        {
            await billing.DisconnectAsync(CancellationToken.None);
        }
    }

    private void Remember(bool isPremium, bool isLifetime)
    {
        if (IsLifetimeCached != isLifetime)
        {
            IsLifetimeCached = isLifetime;
            Microsoft.Maui.Storage.Preferences.Default.Set(LifetimePreferenceKey, isLifetime);
        }

        if (IsPremiumCached == isPremium)
            return;
        IsPremiumCached = isPremium;
        Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, isPremium);
        EntitlementChanged?.Invoke();
    }
}
