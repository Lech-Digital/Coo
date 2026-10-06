using Coo.Mobile.Application.Auth;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// The join flow (Spec 011b §5) as one modal page whose content follows the steps. While it is on the modal stack it owns the
/// sheets (the prompter opens nothing else). Back is Not now, except at the choice, where it only closes: the joining marker
/// stays and the flow opens there again at the next start (§5.3). It closes by itself when the account signs out after the
/// sign-in step (ruling 27: the invitation stays, and the flow reopens with its sign-in step). Every handler catches everything.
/// </summary>
public sealed class JoinPage : ContentPage
{
    private readonly JoinViewModel _model;
    private readonly UndoService _undo;
    private readonly Account _account;
    private readonly ContentView _body = new();
    private bool _started;
    private bool _ended;

    public JoinPage(JoinViewModel model, UndoService undo, Account account)
    {
        (_model, _undo, _account) = (model, undo, account);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Finished += (_, toast) => Dispatcher.Dispatch(async () => await FinishAsync(toast));
        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
        model.BringOver.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
    }

    /// <summary>Set by the prompter: the link that opened Coo held no code (spec §4.1).</summary>
    public bool Malformed { get; set; }

    /// <summary>The page left the modal stack, whatever ended it. Raised once, on the main thread.</summary>
    public event EventHandler? Ended;

    /// <summary>
    /// A new link arrived while the sheet shows (main thread): the flow starts again from the new pending invitation, unless it
    /// is still loading, busy, or at the choice (a join is stored; the new invitation opens once this flow ends).
    /// </summary>
    public void NewLink(bool malformed)
    {
#pragma warning disable CA1031 // Called from a main-thread callback: nothing may escape; the invitation stays pending.
        try
        {
            if (!_started || _model.IsBusy || _model.BringOver.IsBusy || _model.Step is JoinStep.Loading or JoinStep.Choice)
                return;
            if (malformed)
            {
                _model.ShowMalformed();
                return;
            }

            _model.Step = JoinStep.Loading;
            Dispatcher.Dispatch(async () => await RunAsync(_model.StartAsync));
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            _account.Changed -= OnAccountChanged;
            _account.Changed += OnAccountChanged;
            if (_started)
                return;
            _started = true;
            SafeRender();
            if (Malformed)
                _model.ShowMalformed();
            else
                await _model.StartAsync();
        }
        catch (Exception)
        {
            Say(Strings.Get("Error.Generic"));
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    /// <summary>Also when the consent sheet opens over it; the page has ended only once it left the modal stack.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Dispatcher.Dispatch(() =>
        {
#pragma warning disable CA1031 // A dispatched check: nothing may escape.
            try
            {
                if (!Navigation.ModalStack.Contains(this))
                    End();
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        });
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () =>
        {
            if (_model.Step == JoinStep.Choice)
                await CloseAsync();
            else
                await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null));
        });
        return true;
    }

    // Raised from whichever thread changed the account (a refused session is found in the background).
    private void OnAccountChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        if (_account.State is SignedOut && _model.Step != JoinStep.SignIn)
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
        }
#pragma warning restore CA1031
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
        switch (_model.Step)
        {
            case JoinStep.Loading:
                stack.Add(new ActivityIndicator { IsRunning = true });
                break;
            case JoinStep.SignIn:
                stack.Add(Ui.Text(JoinViewModel.SignInTitle, "Title"));
                stack.Add(Ui.Text(JoinViewModel.SignInBody, "Body"));
                stack.Add(Button(JoinViewModel.SignInText, "PrimaryButton", () => _model.SignInCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.NotNowText, "GhostButton", () => _model.NotNowCommand.ExecuteAsync(null)));
                break;
            case JoinStep.Invitation:
                stack.Add(Ui.Text(_model.Title, "Title"));
                stack.Add(Ui.Text(_model.Body, "Body"));
                stack.Add(Ui.Text(_model.Babies, "Body"));
                stack.Add(Button(JoinViewModel.JoinText, "PrimaryButton", () => _model.JoinCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.NotNowText, "GhostButton", () => _model.NotNowCommand.ExecuteAsync(null)));
                break;
            case JoinStep.Message:
                stack.Add(Ui.Text(_model.Message, "Body"));
                if (_model.CanRetry)
                    stack.Add(Button(JoinViewModel.TryAgainText, "PrimaryButton", () => _model.RetryCommand.ExecuteAsync(null)));
                if (_model.CanKeepSeparate)
                    stack.Add(Button(JoinViewModel.KeepSeparateText, "GhostButton", () => _model.KeepSeparateCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.OkText, _model.CanRetry ? "GhostButton" : "PrimaryButton", () =>
                {
                    _model.OkCommand.Execute(null);
                    return Task.CompletedTask;
                }));
                break;
            case JoinStep.Choice:
                stack.Add(BringOverViews.Render(_model.BringOver, RunAsync));
                break;
        }

        if (_model.Toast.Length > 0)
            stack.Add(Ui.Text(_model.Toast, "Meta"));
        if (_model.IsBusy || _model.BringOver.IsBusy)
            stack.Add(new ActivityIndicator { IsRunning = true });
        _body.Content = stack;
    }

    private Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }

    /// <summary>Dispatched from <see cref="JoinViewModel.Finished"/>: nothing may escape, the toast included.</summary>
    private async Task FinishAsync(string toast)
    {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape.
        try
        {
            await CloseAsync();
            if (toast.Length > 0)
                Say(toast);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void Say(string message)
    {
#pragma warning disable CA1031 // A toast that cannot show must not crash the app.
        try
        {
            _undo.Say(message);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            End();
        }
    }

    private void End()
    {
        if (_ended)
            return;
        _ended = true;
        _account.Changed -= OnAccountChanged;
#pragma warning disable CA1031 // The listeners decide what shows next; their failure must not crash the app.
        try
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape; the view models show their own failures.
        catch (Exception)
#pragma warning restore CA1031
        {
            Say(Strings.Get("Error.Generic"));
        }
    }
}
