using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>One plan as shown: its name, its price per period, and a badge (the yearly saving) when there is one.</summary>
public sealed record PremiumPlanRow(PremiumPlan Plan, string Title, string Price, string? Badge, bool HasFreeTrial);

/// <summary>
/// The Premium screen (ADR 0004): what Premium adds, Play's plans and prices, and buying. Buying needs a signed-in Owner
/// or Parent: the purchase is bound to the family on the server, so everyone in it gets Premium (§4.1); signed out, the
/// server could never record it.
/// </summary>
public sealed partial class PremiumViewModel(
    IBillingService billing,
    IHouseholdStore householdStore,
    Account account,
    IFamilyStore families) : ObservableObject
{
    /// <summary>The sign-in, set by the page (it opens Google's sheet over the current activity). True when signed in.</summary>
    public Func<Task<bool>>? SignInAsync { get; set; }

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

    /// <summary>A note that is not an error: a payment still pending, or that there was nothing to restore.</summary>
    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuyLabel))]
    private bool _isSignedIn;

    /// <summary>Only an Owner or Parent buys for the family (ADR 0004 §4.1); a caregiver or viewer sees what Premium is.</summary>
    [ObservableProperty]
    private bool _canBuy = true;

    /// <summary>This Google account holds the subscription, so "Manage subscription" leads somewhere.</summary>
    public bool IsPayer => billing.IsPayerCached;

    /// <summary>The chosen plan's row; null while Play has not answered.</summary>
    public PremiumPlanRow? Selected => Plans.FirstOrDefault(p => p.Plan == SelectedPlan);

    /// <summary>Bought once: nothing renews, so there is no subscription to manage in Play.</summary>
    public bool IsLifetime => billing.IsLifetimeCached;

    /// <summary>The words on the buy button for the chosen plan; signed out, it signs in first.</summary>
    public string BuyLabel => !IsSignedIn ? Strings.Get("Premium.SignInToBuy") : Selected switch
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
            var household = await HouseholdAsync();
            IsSignedIn = account.State is SignedIn or JoinPending;
            CanBuy = await CanBuyAsync(household);
            if (household != null)
                IsPremium = await billing.CheckEntitlementAsync(household.Id, CancellationToken.None);

            Plans = Rows(await billing.GetOffersAsync(CancellationToken.None));
            // Yearly first when Play has it: the best value, and the plan most families keep.
            if (Plans.Count > 0 && Plans.All(p => p.Plan != SelectedPlan))
                SelectedPlan = Plans.Any(p => p.Plan == PremiumPlan.Yearly) ? PremiumPlan.Yearly : Plans[0].Plan;
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(IsPayer));
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

    /// <summary>The family on screen: the store lists it first.</summary>
    private async Task<HouseholdSettings?> HouseholdAsync() =>
        (await householdStore.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);

    private async Task<bool> CanBuyAsync(HouseholdSettings? household)
    {
        if (household is null || account.State is not (SignedIn or JoinPending))
            return true;
        var profile = account.State is SignedIn signedIn ? signedIn.Profile : ((JoinPending)account.State).Profile;
        var memberships = await families.ListAsync(profile.UserId, CancellationToken.None);
        return memberships?.FirstOrDefault(m => m.Household == household.Id) is not { Role: Role.Caregiver or Role.Viewer };
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
        if (IsPremium || Loading || !CanBuy) return;

#if DEBUG
        IsPremium = true;
        billing.SetPremiumForDebug(true);
        return;
#else
        Error = "";
        Notice = "";
        // Signed in first: the purchase belongs to the family on the server, so everyone in it gets Premium.
        if (!IsSignedIn)
        {
            if (SignInAsync is null || !await SignInAsync())
                return;
            await InitializeAsync();
            if (IsPremium || !CanBuy)
                return;
        }

        Loading = true;
        try
        {
            var household = await HouseholdAsync();
            if (household is null)
                return;
            switch (await billing.PurchasePremiumAsync(household.Id, SelectedPlan, CancellationToken.None))
            {
                case PurchaseOutcome.Purchased:
                    IsPremium = true;
                    OnPropertyChanged(nameof(IsPayer));
                    break;
                case PurchaseOutcome.Pending:
                    Notice = Strings.Get("Premium.Pending");
                    break;
                case PurchaseOutcome.Failed:
                    Error = Strings.Get("Premium.PurchaseFailed");
                    break;
                // Cancelled: the person closed Play's sheet; nothing to say.
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

    /// <summary>"Restore purchases": asks Play and the server again, and says so when there is nothing to restore.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Loading)
            return;
        Error = "";
        Notice = "";
        await InitializeAsync();
        if (!IsPremium)
            Notice = Strings.Get("Premium.NothingToRestore");
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
