using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Pages;

public sealed class PremiumPage : ContentPage
{
    private readonly PremiumViewModel _model;
    private readonly ContentView _body = new();

    public PremiumPage(PremiumViewModel model, IServiceProvider services)
    {
        _model = model;
        // Buying needs a signed-in parent: Settings' sign-in, run from here, then the purchase carries on.
        _model.SignInAsync = async () =>
            await services.GetRequiredService<Coo.Mobile.Application.Auth.SignIn>().ExecuteAsync(CancellationToken.None)
                == Coo.Mobile.Application.Auth.SignInResult.SignedIn;
        Shell.SetNavBarIsVisible(this, false);
        
        var root = new Grid();
        root.Add(new Backdrop());
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

    /// <summary>Mochi with a gold crown: the Premium page's picture (design canvas, 2026-11-05).</summary>
    private static View CrownedMochi()
    {
        const double size = 96;
        var grid = new Grid { WidthRequest = size, HeightRequest = size, HorizontalOptions = LayoutOptions.Center };
        grid.Add(new Image { Source = "mochi_awake.png", WidthRequest = size, HeightRequest = size });
        var crown = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M38 20 l6 9 10-12 10 12 6-9 2 16 H36 z")!,
            Fill = new SolidColorBrush(Color.FromArgb("#E8C46E")),
            Aspect = Stretch.None,
            AnchorX = 0,
            AnchorY = 0,
            Scale = size / 108,
            TranslationY = -7,
            InputTransparent = true,
        };
        grid.Add(crown);
        return grid;
    }

    /// <summary>A benefit: its icon on a soft badge, and the line.</summary>
    private static View Benefit(string key, string icon, string tint, string ink)
    {
        var badge = Ui.Rounded(new Icon(icon, ink, 16), tint, 10, new Thickness(7));
        badge.WidthRequest = 30;
        badge.HeightRequest = 30;
        var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12, Padding = new Thickness(16, 10), MinimumHeightRequest = 48 };
        row.Add(badge.Tap(b => b.VerticalOptions = LayoutOptions.Center), 0);
        row.Add(Ui.Text(Strings.Get(key), "Body").Tap(l => { l.FontSize = 14; l.VerticalOptions = LayoutOptions.Center; }), 1);
        return row;
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(PageFrame.Side, 16, PageFrame.Side, 96) };
        stack.Add(Ui.BackHeader("", () => Navigation.PopAsync()));
        stack.Add(CrownedMochi());
        var mark = CooBrand.CreateMark(isPremium: true, isNight: false, fontSize: 30);
        mark.HorizontalOptions = LayoutOptions.Center;
        stack.Add(mark);

        if (_model.Loading)
        {
            stack.Add(Ui.Text(Strings.Get("Premium.Loading"), "Body", "Ink2").Tap(l => l.HorizontalTextAlignment = TextAlignment.Center));
            _body.Content = stack;
            return;
        }

        var intro = !_model.IsPremium ? "Premium.Pitch" : _model.IsLifetime ? "Premium.LifetimeActive" : "Premium.Active";
        stack.Add(Ui.Text(Strings.Get(intro), _model.IsPremium ? "BodyStrong" : "Body", "Ink2").Tap(l => l.HorizontalTextAlignment = TextAlignment.Center));
        var sleep = EventAppearance.For("sleep");
        var peach = EventAppearance.For("feeding");
        stack.Add(PageFrame.Group(
        [
            Benefit("Premium.Benefit.Advisor", IconGeometry.Sparkle, "AccentSoft", "Accent"),
            Benefit("Premium.Benefit.TellCoo", IconGeometry.Mic, "AccentSoft", "Accent"),
            Benefit("Premium.Benefit.Naps", IconGeometry.Sleep, sleep.Tint, sleep.Ink),
            Benefit("Premium.Benefit.Avatar", IconGeometry.Camera, peach.Tint, peach.Ink),
            Benefit("Premium.Benefit.Family", IconGeometry.Family, "AccentSoft", "Accent"),
            Benefit("Premium.Benefit.Support", IconGeometry.Sun, "AccentSoft", "Accent"),
        ]));

        if (_model.IsPremium && !_model.IsLifetime)
        {
            // Only the payer's own Google account has the subscription to manage; everyone else is told who has it.
            if (_model.IsPayer)
            {
                stack.Add(PageFrame.Secondary(Strings.Get("Premium.Manage"), async () => await Launcher.Default.OpenAsync(ManageUri)));
            }
            else
            {
                stack.Add(Ui.Text(Strings.Get("Premium.ViaFamily"), "Meta", "Ink2"));
            }
#if DEBUG
            var expireButton = Ui.Rounded(Ui.Text("DEV: Expire Premium", "BodyStrong", "Ground").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "AccentInk", 8, new Thickness(16));
            expireButton.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.DevExpireCommand });
            stack.Add(expireButton);
#endif
        }
        else if (!_model.IsPremium && !_model.CanBuy)
        {
            stack.Add(Ui.Text(Strings.Get("Premium.OnlyParents"), "Body", "Ink2"));
        }
        else if (!_model.IsPremium)
        {
            // The plans side by side, the chosen one outlined in the accent.
            var plans = new Grid { ColumnSpacing = 8 };
            for (var i = 0; i < _model.Plans.Count; i++)
            {
                plans.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                plans.Add(PlanCard(_model.Plans[i], _model.Plans[i].Plan == _model.SelectedPlan), i);
            }

            stack.Add(plans);

            // Without Play's prices (offline, or Play not set up on this phone) there is nothing to buy yet.
            if (_model.Selected is not null)
            {
                // Coo's loading page under Google Play's sheet, until the server has confirmed the purchase.
                stack.Add(PageFrame.Primary(_model.BuyLabel, () => LoadingOverlay.RunAsync(Strings.Get("Loading.Premium"), () => _model.PurchaseCommand.ExecuteAsync(null))));
                stack.Add(Ui.Text(_model.IsSignedIn ? _model.Terms : Strings.Get("Premium.SignInWhy"), "Meta", "Ink2").Tap(l => l.HorizontalTextAlignment = TextAlignment.Center));
            }
        }

        if (!string.IsNullOrEmpty(_model.Notice))
            stack.Add(Ui.Text(_model.Notice, "Body", "Ink"));
        if (!string.IsNullOrEmpty(_model.Error))
            stack.Add(Ui.Text(_model.Error, "Body", "AccentInk"));

        // Bought on another phone, or before a reinstall: Play's purchases on this account, read again.
        if (!_model.IsPremium && _model.IsSignedIn)
        {
            var restore = Ui.Text(Strings.Get("Premium.Restore"), "Meta", "Accent");
            restore.Padding = new Thickness(0, 8);
            restore.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await LoadingOverlay.RunAsync(Strings.Get("Loading.Premium"), () => _model.RestoreCommand.ExecuteAsync(null))),
            });
            stack.Add(restore);
        }

        _body.Content = stack;
    }

    /// <summary>A plan to tap: its name, price and the saving or trial under it; outlined in the accent when chosen.</summary>
    private View PlanCard(PremiumPlanRow plan, bool chosen)
    {
        var stack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        Label Line(string text, string style, double size, string ink = "Ink") => Ui.Text(text, style, ink).Tap(l =>
        {
            l.FontSize = size;
            l.HorizontalTextAlignment = TextAlignment.Center;
            l.MaxLines = 2;
        });
        stack.Add(Line(plan.Title, "BodyStrong", 13));
        stack.Add(Line(plan.Price, "BodyStrong", 18).Tap(l => l.StyleClass = [Tabular.StyleClass]));
        if (plan.Badge is { } badge)
            stack.Add(Line(badge, "Meta", 11, "Ink2"));
        var card = Ui.Rounded(stack, chosen ? "AccentSoft" : "Glass", 18, new Thickness(6, 12), chosen ? "Accent" : "GlassLine");
        card.StrokeThickness = chosen ? 2 : 1;
        card.MinimumHeightRequest = 92;
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.ChooseCommand, CommandParameter = plan.Plan });
        SemanticProperties.SetDescription(card, $"{plan.Title}, {plan.Price}");
        return card;
    }
}
