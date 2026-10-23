using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

public sealed class PremiumPage : ContentPage
{
    private readonly PremiumViewModel _model;
    private readonly ContentView _body = new();

    public PremiumPage(PremiumViewModel model)
    {
        _model = model;
        Shell.SetNavBarIsVisible(this, false);
        
        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        Content = root;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnModelChanged;
        
        Render();
        
        await _model.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Dispatch(Render);
    }

    /// <summary>Google Play's page for this subscription, where it is changed or cancelled (Play subscriptions policy).</summary>
    private static readonly Uri ManageUri =
        new("https://play.google.com/store/account/subscriptions?sku=premium_subscription&package=com.lechdigital.coo");

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Premium.Title"), () => Navigation.PopAsync()));

        if (_model.Loading)
        {
            stack.Add(Ui.Text(Strings.Get("Premium.Loading"), "Body", "Ink2"));
            _body.Content = stack;
            return;
        }

        var intro = !_model.IsPremium ? "Premium.Pitch" : _model.IsLifetime ? "Premium.LifetimeActive" : "Premium.Active";
        stack.Add(Ui.Text(Strings.Get(intro), _model.IsPremium ? "BodyStrong" : "Body"));
        var benefits = new VerticalStackLayout { Spacing = 10 };
        foreach (var key in new[] { "Premium.Benefit.Advisor", "Premium.Benefit.Avatar", "Premium.Benefit.Family", "Premium.Benefit.Support" })
        {
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10 };
            row.Add(Ui.Text("✓", "BodyStrong", "Accent"), 0);
            row.Add(Ui.Text(Strings.Get(key), "Body"), 1);
            benefits.Add(row);
        }

        stack.Add(new Card(benefits));

        if (_model.IsPremium && !_model.IsLifetime)
        {
            var manage = Ui.Rounded(Ui.Text(Strings.Get("Premium.Manage"), "BodyStrong").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "Surface", 8, new Thickness(16), "Line");
            manage.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Launcher.Default.OpenAsync(ManageUri)) });
            stack.Add(manage);
#if DEBUG
            var expireButton = Ui.Rounded(Ui.Text("DEV: Expire Premium", "BodyStrong", "Ground").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "AccentInk", 8, new Thickness(16));
            expireButton.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.DevExpireCommand });
            stack.Add(expireButton);
#endif
        }
        else if (!_model.IsPremium)
        {
            foreach (var plan in _model.Plans)
                stack.Add(PlanCard(plan, plan.Plan == _model.SelectedPlan));

            // Without Play's prices (offline, or Play not set up on this phone) there is nothing to buy yet.
            if (_model.Selected is not null)
            {
                var button = Ui.Rounded(Ui.Text(_model.BuyLabel, "BodyStrong", "Ground").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "Accent", 8, new Thickness(16));
                button.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.PurchaseCommand });
                stack.Add(button);
                stack.Add(Ui.Text(_model.Terms, "Meta", "Ink2"));
            }
        }

        if (!string.IsNullOrEmpty(_model.Error))
            stack.Add(Ui.Text(_model.Error, "Body", "AccentInk"));

        _body.Content = stack;
    }

    /// <summary>A plan to tap: its name and price, the yearly saving as a badge, outlined in the accent when chosen.</summary>
    private View PlanCard(PremiumPlanRow plan, bool chosen)
    {
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 10 };
        grid.Add(Ui.Text(plan.Title, "BodyStrong"), 0, 0);
        grid.Add(Ui.Text(plan.Price, "Body", "Ink2"), 0, 1);
        if (plan.Badge is { } badge)
        {
            var pill = Ui.Rounded(Ui.Text(badge, "Meta", "OnAccent"), "Accent", 10, new Thickness(8, 3));
            pill.VerticalOptions = LayoutOptions.Center;
            grid.Add(pill, 1, 0);
            Grid.SetRowSpan(pill, 2);
        }

        var card = Ui.Rounded(grid, chosen ? "AccentSoft" : "Surface", 14, new Thickness(16, 12), chosen ? "Accent" : "Line");
        card.StrokeThickness = chosen ? 2 : 1;
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.ChooseCommand, CommandParameter = plan.Plan });
        SemanticProperties.SetDescription(card, $"{plan.Title}, {plan.Price}");
        return card;
    }
}
