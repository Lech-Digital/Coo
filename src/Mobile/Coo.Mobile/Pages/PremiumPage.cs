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

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Premium.Title") ?? "Coo Premium", () => Navigation.PopAsync()));

        if (_model.Loading)
        {
            stack.Add(Ui.Text(Strings.Get("Premium.Loading") ?? "Loading...", "Body", "Ink2"));
        }
        else if (_model.IsPremium)
        {
            stack.Add(Ui.Text(Strings.Get("Premium.Active") ?? "You have an active Premium subscription. Thank you for your support!", "BodyStrong"));
#if DEBUG
            var expireButton = Ui.Rounded(Ui.Text("DEV: Expire Premium", "BodyStrong", "Ground").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "Error", 8, new Thickness(16));
            expireButton.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.DevExpireCommand });
            stack.Add(expireButton);
#endif
        }
        else
        {
            stack.Add(Ui.Text(Strings.Get("Premium.Pitch") ?? "Get Coo Premium to unlock advanced features and support the development.", "Body"));
            
            var price = _model.Price;
            if (!string.IsNullOrEmpty(price))
            {
                stack.Add(Ui.Text($"{price} / month", "BodyStrong", "Accent"));
            }

            var button = Ui.Rounded(Ui.Text(Strings.Get("Premium.Subscribe") ?? "Subscribe", "BodyStrong", "Ground").Tap(l => l.HorizontalOptions = LayoutOptions.Center), "Accent", 8, new Thickness(16));
            button.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.PurchaseCommand });
            stack.Add(button);
        }
        
        if (!string.IsNullOrEmpty(_model.Error))
        {
            stack.Add(Ui.Text(_model.Error, "Body", "Error"));
        }

        _body.Content = stack;
    }
}
