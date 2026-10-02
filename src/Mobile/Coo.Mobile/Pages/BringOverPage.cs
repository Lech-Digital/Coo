using Coo.Mobile.Application.Auth;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// "Bring my entries over" / "Keep them separate" (Spec 011 §4), then where each local baby goes. Back is "Keep". It
/// closes by itself when the account signs out while it is open. Every handler is async void and catches everything.
/// </summary>
public sealed class BringOverPage : ContentPage
{
    private readonly BringOverViewModel _model;
    private readonly Account _account;
    private readonly ContentView _body = new();
    private bool _loaded;

    public BringOverPage(BringOverViewModel model, Account account)
    {
        _model = model;
        _account = account;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BringOverViewModel.IsMapping) or nameof(BringOverViewModel.Toast) or nameof(BringOverViewModel.Mappings))
                Dispatcher.Dispatch(SafeRender);
        };
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void (PF3): nothing may escape, Render included.
        try
        {
            base.OnAppearing();
            _account.Changed += OnAccountChanged;
            if (_account.State is SignedOut)
            {
                // Dispatched, as ConsentPage does: popping a modal from inside its own appearing is not safe.
                Dispatcher.Dispatch(async () => await CloseAsync());
                return;
            }

            if (_loaded)
                return;
            _loaded = true;
            await RunAsync(_model.LoadAsync);
            Render();
        }
        catch (Exception)
        {
            // The sheet shows what it has; Keep (or Back) still closes it.
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
#pragma warning disable CA1031 // Back is Keep: a failure must not crash the app.
        try
        {
            _model.KeepCommand.Execute(null);
        }
        catch (Exception)
        {
            // Nothing changed; the sheet stays and Keep can be tapped.
        }
#pragma warning restore CA1031

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
            Render();
        }
        catch (Exception)
        {
            // The sheet keeps what it showed.
        }
#pragma warning restore CA1031
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
        stack.Add(Ui.Text(_model.Summary, "Title"));
        stack.Add(Ui.Text(_model.Family, "Body"));
        if (!_model.IsMapping)
        {
            stack.Add(Primary(BringOverViewModel.BringText, () => _model.BringCommand.ExecuteAsync(null)));
            stack.Add(Ghost(BringOverViewModel.KeepText, () =>
            {
                _model.KeepCommand.Execute(null);
                return Task.CompletedTask;
            }));
        }
        else
        {
            foreach (var mapping in _model.Mappings)
            {
                var picker = new Picker { ItemsSource = mapping.Options.Select(o => o.Label).ToList(), SelectedIndex = mapping.Selected };
                picker.SelectedIndexChanged += (_, _) => mapping.Selected = picker.SelectedIndex;
                stack.Add(Ui.Text(mapping.Prompt, "BodyStrong"));
                stack.Add(picker);
            }

            stack.Add(Primary(BringOverViewModel.ConfirmText, () => _model.ConfirmCommand.ExecuteAsync(null)));
        }

        if (_model.Toast.Length > 0)
            stack.Add(Ui.Text(_model.Toast, "Meta"));
        _body.Content = stack;
    }

    private static Button Primary(string text, Func<Task> action) => Styled(text, "PrimaryButton", action);

    private static Button Ghost(string text, Func<Task> action) => Styled(text, "GhostButton", action);

    private static Button Styled(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice (Keep and a sign-out together) must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape; the view model already shows its own failures.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
