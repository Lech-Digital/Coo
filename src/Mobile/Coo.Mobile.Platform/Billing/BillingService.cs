using Coo.Domain.Common;
using Coo.Mobile.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Plugin.InAppBilling;

namespace Coo.Mobile.Platform.Billing;

/// <summary>
/// Premium through Google Play (Spec 012): a subscription with a monthly and a yearly base plan, or a one-time lifetime
/// product. A purchase is made with the household's ID as Play's obfuscated account ID and handed to the server, which
/// verifies it, gives Premium to the whole family and acknowledges it with Google; this phone acknowledges it too, but
/// only once the server has it (grant, then acknowledge: ADR 0004 §4), and remembers it, so Premium shows offline.
/// </summary>
public class BillingService(IServiceProvider? services = null) : IBillingService
{
    public const string SubscriptionId = "premium_subscription";
    public const string LifetimeId = "premium_lifetime";
    private const string PremiumPreferenceKey = "billing.is_premium";
    private const string LifetimePreferenceKey = "billing.is_lifetime";
    private const string PayerPreferenceKey = "billing.is_payer";
    private const string ConfirmedPreferenceKey = "billing.confirmed_at";

    /// <summary>
    /// How long Premium heard from Play or the server holds without hearing it again (ADR 0004 §8: offline, the cached
    /// answer holds a few days past its period). A month: long enough for a phone left offline, short enough that a
    /// partner's cancelled subscription does not hold here for good.
    /// </summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(35);

    private bool _isPremium = Microsoft.Maui.Storage.Preferences.Default.Get(PremiumPreferenceKey, false);

    public bool IsPremiumCached => _isPremium && (IsLifetimeCached || !IsStale);

    public bool IsLifetimeCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(LifetimePreferenceKey, false);

    public bool IsPayerCached { get; private set; } = Microsoft.Maui.Storage.Preferences.Default.Get(PayerPreferenceKey, false);

    public event Action? EntitlementChanged;

    // Resolved late: the billing API is registered by Infrastructure, after Platform.
    private IBillingApi? Api => services?.GetService<IBillingApi>();

    // The host's clock (only the composition root makes the real one); without services, nothing goes stale.
    private TimeProvider? Time => services?.GetService<TimeProvider>();

    private bool IsStale
    {
        get
        {
            if (Time is not { } time)
                return false;
            var confirmed = Microsoft.Maui.Storage.Preferences.Default.Get(ConfirmedPreferenceKey, 0L);
            // Never recorded (a phone from before 1.9.0): counts from now, so an update does not take Premium away.
            if (confirmed == 0)
            {
                Microsoft.Maui.Storage.Preferences.Default.Set(ConfirmedPreferenceKey, time.GetUtcNow().ToUnixTimeMilliseconds());
                return false;
            }

            return time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(confirmed) > OfflineGrace;
        }
    }

    public void SetPremiumForDebug(bool isPremium) => Remember(isPremium, false, false);

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

    public async Task<PurchaseOutcome> PurchasePremiumAsync(HouseholdId householdId, PremiumPlan plan, CancellationToken cancellationToken)
    {
        var billing = CrossInAppBilling.Current;
        try
        {
#pragma warning disable CA2016
            if (!await billing.ConnectAsync(true))
                return PurchaseOutcome.Failed;

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
                    return PurchaseOutcome.Failed;
                offerToken = chosen.OfferToken;
            }

            var purchase = await billing.PurchaseAsync(productId, lifetime ? ItemType.InAppPurchase : ItemType.Subscription, householdId.Value.ToString(), null, offerToken);
#pragma warning restore CA2016
            switch (purchase?.State)
            {
                case PurchaseState.Purchased:
                    Remember(true, lifetime || IsLifetimeCached, true);
                    await RecordThenAcknowledgeAsync(billing, householdId, purchase, cancellationToken);
                    return PurchaseOutcome.Purchased;
                case PurchaseState.PaymentPending or PurchaseState.Deferred:
                    // Not paid yet, and not to be acknowledged yet: CheckEntitlementAsync finds it once it is paid.
                    return PurchaseOutcome.Pending;
                default:
                    return PurchaseOutcome.Failed;
            }
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.UserCancelled)
        {
            return PurchaseOutcome.Cancelled;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.AlreadyOwned)
        {
            // Bought before on this Google account (another phone, or before a reinstall): that purchase counts.
            return await CheckEntitlementAsync(householdId, cancellationToken) ? PurchaseOutcome.Purchased : PurchaseOutcome.Failed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to purchase premium: {ex}");
            return PurchaseOutcome.Failed;
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
                Remember(true, lifetime is not null, true);
                // Again on every check: a purchase whose report did not reach the server (offline) gets there now, the
                // server keeps the expiry up to date, and a purchase never acknowledged (paid after a pending payment, or
                // the app closed mid-purchase) is acknowledged before Play's three days run out and it is refunded.
                await RecordThenAcknowledgeAsync(billing, householdId, own, cancellationToken);
                return true;
            }

            // Not bought on this Google account: another member of the family may have bought it.
            // Unknown (offline, signed out) keeps what this phone remembers, for OfflineGrace.
            if (Api is { } server && await server.CheckEntitlementAsync(householdId.Value, cancellationToken) is { } isPremium)
                Remember(isPremium, false, false);
            else if (connected && IsPayerCached)
                Remember(false, false, false); // Play answered, and this account's own purchase is gone (expired or refunded).

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

    /// <summary>
    /// Reports the purchase to the server, then acknowledges it with Play only if the server has it: if the server is never
    /// reached, Play refunds the purchase after three days rather than charging for a family that never got Premium. The
    /// server acknowledges it too; acknowledging twice is harmless.
    /// </summary>
    private async Task RecordThenAcknowledgeAsync(IInAppBilling billing, HouseholdId householdId, InAppBillingPurchase purchase, CancellationToken cancellationToken)
    {
        var recorded = Api is { } api && await api.VerifyReceiptAsync(householdId.Value, purchase.ProductId, purchase.PurchaseToken, cancellationToken);
        if (!recorded || purchase.IsAcknowledged.GetValueOrDefault())
            return;
        try
        {
            // Lifetime is acknowledged, never consumed: consuming it would let it be bought again and lose it from this
            // account's purchases.
#pragma warning disable CA2016
            await billing.FinalizePurchaseAsync([purchase.PurchaseToken]);
#pragma warning restore CA2016
        }
        catch (Exception ex)
        {
            // The server acknowledged it already, or does on its hourly recheck.
            System.Diagnostics.Debug.WriteLine($"Could not acknowledge a purchase: {ex.Message}");
        }
    }

    private void Remember(bool isPremium, bool isLifetime, bool isPayer)
    {
        if (Time is { } time)
            Microsoft.Maui.Storage.Preferences.Default.Set(ConfirmedPreferenceKey, time.GetUtcNow().ToUnixTimeMilliseconds());
        if (IsPayerCached != isPayer)
        {
            IsPayerCached = isPayer;
            Microsoft.Maui.Storage.Preferences.Default.Set(PayerPreferenceKey, isPayer);
        }

        if (IsLifetimeCached != isLifetime)
        {
            IsLifetimeCached = isLifetime;
            Microsoft.Maui.Storage.Preferences.Default.Set(LifetimePreferenceKey, isLifetime);
        }

        if (_isPremium == isPremium)
            return;
        _isPremium = isPremium;
        Microsoft.Maui.Storage.Preferences.Default.Set(PremiumPreferenceKey, isPremium);
        EntitlementChanged?.Invoke();
    }
}
