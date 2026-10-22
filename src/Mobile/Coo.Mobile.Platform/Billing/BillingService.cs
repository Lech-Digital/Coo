using Coo.Domain.Common;
using Coo.Mobile.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Plugin.InAppBilling;

namespace Coo.Mobile.Platform.Billing;

/// <summary>
/// Premium through Google Play (Spec 012). A purchase is made with the household's ID as Play's obfuscated account ID
/// and handed to the server, which verifies it and gives Premium to the whole family; this phone also remembers it,
/// so Premium shows offline.
/// </summary>
public class BillingService(IServiceProvider? services = null) : IBillingService
{
    public const string SubscriptionId = "premium_subscription";
    private const string PremiumPreferenceKey = "billing.is_premium";

    public bool IsPremiumCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(PremiumPreferenceKey, false);

    public event Action? EntitlementChanged;

    // Resolved late: the billing API is registered by Infrastructure, after Platform.
    private IBillingApi? Api => services?.GetService<IBillingApi>();

    public void SetPremiumForDebug(bool isPremium) => Remember(isPremium);

    public async Task<string?> GetPremiumPriceAsync(CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            if (!await billing.ConnectAsync(true))
                return null;
            var products = await billing.GetProductInfoAsync(ItemType.Subscription, [SubscriptionId]);
#pragma warning restore CA2016
            return products?.FirstOrDefault()?.LocalizedPrice;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get premium price: {ex}");
            return null;
        }
        finally
        {
            await billing.DisconnectAsync(CancellationToken.None);
        }
    }

    public async Task<bool> PurchasePremiumAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            if (!await billing.ConnectAsync(true))
                return false;

            var purchase = await billing.PurchaseAsync(SubscriptionId, ItemType.Subscription, householdId.Value.ToString());
            if (purchase is not { State: PurchaseState.Purchased })
                return false;

            // Unacknowledged purchases are refunded by Play after three days.
            if (!purchase.IsAcknowledged.GetValueOrDefault())
                await billing.FinalizePurchaseAsync([purchase.PurchaseToken]);
#pragma warning restore CA2016

            Remember(true);
            if (Api is { } api)
                await api.VerifyReceiptAsync(householdId.Value, SubscriptionId, purchase.PurchaseToken, cancellationToken);
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
            var purchases = connected ? await billing.GetPurchasesAsync(ItemType.Subscription) : null;
#pragma warning restore CA2016
            var own = purchases?.FirstOrDefault(p => p.ProductId == SubscriptionId && p.State == PurchaseState.Purchased);
            if (own is not null)
            {
                Remember(true);
                // Again on every check: a purchase whose report did not reach the server (offline) gets there now,
                // and the server keeps the expiry up to date. It refuses a purchase made for another family.
                if (Api is { } api)
                    await api.VerifyReceiptAsync(householdId.Value, SubscriptionId, own.PurchaseToken, cancellationToken);
                return true;
            }

            // Not bought on this Google account: another member of the family may have bought it.
            // Unknown (offline, signed out) keeps what this phone remembers.
            if (Api is { } server && await server.CheckEntitlementAsync(householdId.Value, cancellationToken) is { } isPremium)
                Remember(isPremium);

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

    private void Remember(bool isPremium)
    {
        if (IsPremiumCached == isPremium)
            return;
        IsPremiumCached = isPremium;
        Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, isPremium);
        EntitlementChanged?.Invoke();
    }
}
