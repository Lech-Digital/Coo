using Coo.Domain.Common;
using Coo.Mobile.Application.Billing;
using Plugin.InAppBilling;

namespace Coo.Mobile.Platform.Billing;

public class BillingService : IBillingService
{
    private const string SubscriptionId = "premium_subscription";
    private const string PremiumPreferenceKey = "billing.is_premium";
    private readonly IServiceProvider? _services;

    public BillingService(IServiceProvider? services = null)
    {
        _services = services;
    }

    public bool IsPremiumCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(PremiumPreferenceKey, false);

    public event Action? EntitlementChanged;

    public void SetPremiumForDebug(bool isPremium)
    {
        if (IsPremiumCached != isPremium)
        {
            IsPremiumCached = isPremium;
            Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, isPremium);
            EntitlementChanged?.Invoke();
        }
    }

    public async Task<string?> GetPremiumPriceAsync(CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            var connected = await billing.ConnectAsync(true);
            if (!connected)
                return null;

            var productInfo = await billing.GetProductInfoAsync(ItemType.Subscription, new[] { SubscriptionId });
#pragma warning restore CA2016
            var product = productInfo?.FirstOrDefault();
            return product?.LocalizedPrice;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get premium price: {ex}");
            return null;
        }
        finally
        {
#pragma warning disable CA2016
            await billing.DisconnectAsync();
#pragma warning restore CA2016
        }
    }

    public async Task<bool> PurchasePremiumAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            var connected = await billing.ConnectAsync(true);
            if (!connected)
                return false;

            var purchase = await billing.PurchaseAsync(SubscriptionId, ItemType.Subscription, householdId.Value.ToString());
            if (purchase != null && purchase.State == PurchaseState.Purchased)
            {
                if (!purchase.IsAcknowledged.GetValueOrDefault())
                {
                    await billing.FinalizePurchaseAsync(new[] { purchase.PurchaseToken });
                }
                var wasPremium = IsPremiumCached;
                IsPremiumCached = true;
                Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, true);
                if (!wasPremium)
                    EntitlementChanged?.Invoke();
                return true;
            }
#pragma warning restore CA2016
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to purchase premium: {ex}");
        }
        finally
        {
#pragma warning disable CA2016
            await billing.DisconnectAsync();
#pragma warning restore CA2016
        }

        return false;
    }

    public async Task<bool> CheckEntitlementAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            var connected = await billing.ConnectAsync(true);
            if (!connected)
                return IsPremiumCached;

            var purchases = await billing.GetPurchasesAsync(ItemType.Subscription);
            var hadChange = false;
            if (purchases != null && purchases.Any(p => p.ProductId == SubscriptionId && p.State == PurchaseState.Purchased))
            {
                hadChange = !IsPremiumCached;
                IsPremiumCached = true;
                Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, true);
                if (hadChange)
                    EntitlementChanged?.Invoke();
                return true;
            }
#pragma warning restore CA2016
            
            hadChange = IsPremiumCached;

            // If Google Play store has no active subscription on this Google account, check backend household subscription
            if (_services != null)
            {
                var advisorApi = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Coo.Mobile.Application.Advisor.IAdvisorApi>(_services);
                if (advisorApi != null)
                {
                    var isServerPremium = await advisorApi.CheckEntitlementAsync(householdId.Value, cancellationToken);
                    if (isServerPremium)
                    {
                        hadChange = !IsPremiumCached;
                        IsPremiumCached = true;
                        Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, true);
                        if (hadChange)
                            EntitlementChanged?.Invoke();
                        return true;
                    }
                }
            }

            IsPremiumCached = false;
            Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, false);
            if (hadChange)
                EntitlementChanged?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to check entitlement: {ex}");
            return IsPremiumCached;
        }
        finally
        {
#pragma warning disable CA2016
            await billing.DisconnectAsync();
#pragma warning restore CA2016
        }
    }
}
