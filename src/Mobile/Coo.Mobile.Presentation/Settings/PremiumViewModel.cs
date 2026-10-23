using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>One plan as shown: its name, its price per period, and a badge (the yearly saving) when there is one.</summary>
public sealed record PremiumPlanRow(PremiumPlan Plan, string Title, string Price, string? Badge, bool HasFreeTrial);

public sealed partial class PremiumViewModel(
    IBillingService billing,
    IHouseholdStore householdStore) : ObservableObject
{
    [ObservableProperty]
    private bool _loading;

    [ObservableProperty]
    private IReadOnlyList<PremiumPlanRow> _plans = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    private PremiumPlan _selectedPlan = PremiumPlan.Yearly;

    [ObservableProperty]
    private bool _isPremium;

    [ObservableProperty]
    private string _error = "";

    /// <summary>The chosen plan's row; null while Play has not answered.</summary>
    public PremiumPlanRow? Selected => Plans.FirstOrDefault(p => p.Plan == SelectedPlan);

    /// <summary>Bought once: nothing renews, so there is no subscription to manage in Play.</summary>
    public bool IsLifetime => billing.IsLifetimeCached;

    /// <summary>The words on the buy button for the chosen plan.</summary>
    public string BuyLabel => Selected switch
    {
        { Plan: PremiumPlan.Lifetime } => Strings.Get("Premium.BuyLifetime"),
        { HasFreeTrial: true } => Strings.Get("Premium.StartTrial"),
        _ => Strings.Get("Premium.Subscribe"),
    };

    /// <summary>The terms under the button: renews and how to cancel, or a single payment.</summary>
    public string Terms => Selected switch
    {
        null => "",
        { Plan: PremiumPlan.Lifetime } => Strings.Get("Premium.LifetimeTerms"),
        { HasFreeTrial: true } row => Strings.Format("Premium.TrialTerms", row.Price),
        var row => Strings.Format("Premium.Price", row.Price),
    };

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

            Plans = Rows(await billing.GetOffersAsync(CancellationToken.None));
            // Yearly first when Play has it: the best value, and the plan most families keep.
            if (Plans.Count > 0 && Plans.All(p => p.Plan != SelectedPlan))
                SelectedPlan = Plans.Any(p => p.Plan == PremiumPlan.Yearly) ? PremiumPlan.Yearly : Plans[0].Plan;
            OnPropertyChanged(nameof(Selected));
        }
        catch (Exception ex)
        {
            Error = Strings.Get("Error.Generic");
            System.Diagnostics.Debug.WriteLine($"Error initializing premium: {ex}");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>The plans in a fixed order, the yearly one with how much it saves against twelve months.</summary>
    public static IReadOnlyList<PremiumPlanRow> Rows(IReadOnlyList<PremiumOffer> offers)
    {
        var monthly = offers.FirstOrDefault(o => o.Plan == PremiumPlan.Monthly);
        return [.. offers.OrderBy(o => o.Plan).Select(o =>
        {
            string? badge = null;
            if (o.Plan == PremiumPlan.Yearly && monthly is { PriceMicros: > 0 } && o.PriceMicros > 0)
            {
                var saving = (int)Math.Floor(100 - (o.PriceMicros * 100.0 / (monthly.PriceMicros * 12)));
                if (saving > 0)
                    badge = Strings.Format("Premium.Save", saving);
            }

            var (title, price) = o.Plan switch
            {
                PremiumPlan.Monthly => (Strings.Get("Premium.Plan.Monthly"), Strings.Format("Premium.PerMonth", o.Price)),
                PremiumPlan.Yearly => (Strings.Get("Premium.Plan.Yearly"), Strings.Format("Premium.PerYear", o.Price)),
                _ => (Strings.Get("Premium.Plan.Lifetime"), Strings.Format("Premium.Once", o.Price)),
            };
            return new PremiumPlanRow(o.Plan, title, price, badge, o.HasFreeTrial);
        })];
    }

    [RelayCommand]
    private void Choose(PremiumPlan plan) => SelectedPlan = plan;

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
                var success = await billing.PurchasePremiumAsync(household.Id, SelectedPlan, CancellationToken.None);
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
            Error = Strings.Get("Error.Generic");
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
