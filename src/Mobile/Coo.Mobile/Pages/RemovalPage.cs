using Coo.Mobile.Application.Auth;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.Pages;

/// <summary>
/// "You're no longer in this family" (spec §8.2), over whatever shows. Nothing is deleted before OK. Back closes without
/// deleting; the prompter opens it again at the next start or resume. The share sheet keeps this page open. It closes by
/// itself when the account signs out while it is open. Every handler catches everything.
/// </summary>
public sealed class RemovalPage : ContentPage
{
    private readonly RemovalViewModel _model;
    private readonly Account _account;
    private readonly SheetNavigator _sheets;
    private readonly ContentView _body = new();
    private bool _loaded;
    private bool _closeWanted;

    public RemovalPage(RemovalViewModel model, Account account, SheetNavigator sheets)
    {
        (_model, _account, _sheets) = (model, account, sheets);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            _account.Changed -= OnAccountChanged;
            _account.Changed += OnAccountChanged;
            if (_account.State is SignedOut || _closeWanted)
            {
                // Dispatched, as ConsentPage does: popping a modal from inside its own appearing is not safe.
                Dispatcher.Dispatch(async () => await CloseAsync());
                return;
            }

            if (_loaded)
                return;
            _loaded = true;
            await _model.LoadAsync();
            SafeRender();
        }
        catch (Exception)
        {
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _account.Changed -= OnAccountChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    // Raised from whichever thread changed the account (a refused session is found in the background).
    private void OnAccountChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        if (_account.State is SignedOut)
            await CloseAsync();
    });

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
            stack.Add(Ui.Text(RemovalViewModel.Title, "Title"));
            stack.Add(Ui.Text(_model.Body, "Body"));
            if (_model.Unshared.Length > 0)
            {
                stack.Add(Ui.Text(_model.Unshared, "BodyStrong"));
                stack.Add(Button(RemovalViewModel.ShareText, "GhostButton", () => _model.ShareCommand.ExecuteAsync(null)));
            }

            stack.Add(Button(RemovalViewModel.OkText, "PrimaryButton", () => _model.OkCommand.ExecuteAsync(null)));
            if (_model.Toast.Length > 0)
                stack.Add(Ui.Text(_model.Toast, "Meta"));
            if (_model.IsBusy)
                stack.Add(new ActivityIndicator { IsRunning = true });
            _body.Content = stack;
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) =>
        {
            try
            {
                await action();
            }
#pragma warning disable CA1031 // async void handlers: nothing may escape.
            catch (Exception)
#pragma warning restore CA1031
            {
                Dispatcher.Dispatch(() =>
                {
                    _model.Toast = Strings.Get("Error.Generic");
                });
            }
        };
        return button;
    }

    /// <summary>Pops this page, never another one (walk fix F1); with a sheet over it, the close waits until this page appears again (M1).</summary>
    private async Task CloseAsync()
    {
        try
        {
            _closeWanted = await _sheets.CloseAsync(this) == SheetClose.Covered;
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
