using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public sealed partial class PremiumViewModel(
    IBillingService billing,
    IHouseholdStore householdStore) : ObservableObject
{
    [ObservableProperty]
    private bool _loading;

    [ObservableProperty]
    private string _price = "";

    [ObservableProperty]
    private bool _isPremium;

    [ObservableProperty]
    private string _error = "";

    public async Task InitializeAsync()
    {
        Loading = true;
        Error = "";
        try
        {
            var households = await householdStore.ListHouseholdsAsync(CancellationToken.None);
            var household = households.Count > 0 ? households[0] : null;
            if (household != null)
            {
                IsPremium = await billing.CheckEntitlementAsync(household.Id, CancellationToken.None);
            }
            Price = (await billing.GetPremiumPriceAsync(CancellationToken.None)) ?? "";
        }
        catch (Exception ex)
        {
            Error = Strings.Get("Error.General");
            System.Diagnostics.Debug.WriteLine($"Error initializing premium: {ex}");
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    private async Task PurchaseAsync()
    {
        if (IsPremium) return;

#if DEBUG
        IsPremium = true;
        billing.SetPremiumForDebug(true);
        return;
#else
        Loading = true;
        Error = "";
        try
        {
            var households = await householdStore.ListHouseholdsAsync(CancellationToken.None);
            var household = households.Count > 0 ? households[0] : null;
            if (household != null)
            {
                var success = await billing.PurchasePremiumAsync(household.Id, CancellationToken.None);
                if (success)
                {
                    IsPremium = true;
                }
                else
                {
                    Error = Strings.Get("Premium.PurchaseFailed");
                }
            }
        }
        catch (Exception ex)
        {
            Error = Strings.Get("Error.General");
            System.Diagnostics.Debug.WriteLine($"Error purchasing premium: {ex}");
        }
        finally
        {
            Loading = false;
        }
#endif
    }

#if DEBUG
    [RelayCommand]
    private void DevExpire()
    {
        IsPremium = false;
        billing.SetPremiumForDebug(false);
    }
#endif
}
