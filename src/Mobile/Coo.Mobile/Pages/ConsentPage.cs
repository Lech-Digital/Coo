using Coo.Mobile.Application.Auth;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// The consent sheet (Spec 011 §3) as a modal page, so it opens over whatever is showing (ruling 15). Back is "Not now".
/// It closes by itself when the account signs out while it is open. Every handler is async void and catches everything.
/// </summary>
public sealed class ConsentPage : ContentPage
{
    private readonly ConsentViewModel _model;
    private readonly Account _account;
    private readonly Label _toast = new() { IsVisible = false };

    public ConsentPage(ConsentViewModel model, Account account)
    {
        _model = model;
        _account = account;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConsentViewModel.Toast))
                Dispatcher.Dispatch(() => ShowToast(_model.Toast));
        };

        var privacy = Ui.Text(ConsentViewModel.PrivacyText, "BodyStrong", "Accent");
        privacy.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await RunAsync(() => _model.OpenPrivacyCommand.ExecuteAsync(null))) });
        var agree = new Button { Text = ConsentViewModel.AgreeText };
        agree.SetDynamicResource(StyleProperty, "PrimaryButton");
        agree.Clicked += async (_, _) => await RunAsync(() => _model.AgreeCommand.ExecuteAsync(null));
        var notNow = new Button { Text = ConsentViewModel.NotNowText };
        notNow.SetDynamicResource(StyleProperty, "GhostButton");
        notNow.Clicked += async (_, _) => await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null));
        _toast.SetDynamicResource(StyleProperty, "Meta");

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(24, 48, 24, 32),
                Children =
                {
                    Ui.Text(ConsentViewModel.Title, "Title"),
                    Ui.Text(ConsentViewModel.Body, "Body"),
                    Ui.Text(ConsentViewModel.Body2, "Body"),
                    privacy,
                    _toast,
                    agree,
                    notNow,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _account.Changed += OnAccountChanged;

        // Signed out while the sheet was opening (or while the privacy policy was in front): nothing left to ask.
        if (_account.State is SignedOut)
            Dispatcher.Dispatch(async () => await CloseAsync());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _account.Changed -= OnAccountChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null)));
        return true;
    }

    // Raised from whichever thread changed the account (a refused session is found in the background).
    private void OnAccountChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        if (_account.State is SignedOut)
            await CloseAsync();
    });

    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.IsVisible = text.Length > 0;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice (a double tap, or Agree and a sign-out together) must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape.
        catch (Exception)
#pragma warning restore CA1031
        {
            ShowToast(Strings.Get("Error.Generic"));
        }
    }
}
