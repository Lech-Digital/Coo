using Coo.Domain.Common;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// The Family page (Spec 011b §2), pushed from Settings → Family → Members. Every confirmation is Android's dialog, asked
/// once; every handler catches everything. It closes only itself.
/// </summary>
public sealed class FamilyPage : ContentPage
{
    private readonly FamilyPageViewModel _model;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private HouseholdId _household;
    private bool _attached;
    private bool _confirming;
    private bool _renderQueued;

    public FamilyPage(FamilyPageViewModel model, UndoService undo)
    {
        (_model, _undo) = (model, undo);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        Content = root;
    }

    public static FamilyPage Open(IServiceProvider services, HouseholdId household)
    {
        ArgumentNullException.ThrowIfNull(services);
        var page = services.GetRequiredService<FamilyPage>();
        page._household = household;
        return page;
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            Attach();
            await _model.LoadAsync(_household);
            SafeRender();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Disappearing is also raised by a sheet opening over the page, while a command may still be running: the page stops
    /// listening once it has left the stack.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Dispatcher.Dispatch(() =>
        {
#pragma warning disable CA1031 // A dispatched check: nothing may escape.
            try
            {
                if (!Navigation.NavigationStack.Contains(this))
                    Detach();
            }
            catch (Exception)
            {
                Detach();
            }
#pragma warning restore CA1031
        });
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await PopAsync());
        return true;
    }

    private void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _model.PropertyChanged += OnModelChanged;
        _model.Closed += OnClosed;
    }

    private void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
        _model.Closed -= OnClosed;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(() =>
    {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape.
        try
        {
            if (e.PropertyName == nameof(FamilyPageViewModel.Toast))
            {
                if (_model.Toast.Length > 0)
                    _undo.Say(_model.Toast);
                return;
            }

            QueueRender();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    });

    /// <summary>A load sets half a dozen properties; they become one redraw, not six (like Settings).</summary>
    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
            SafeRender();
        });
    }

    // The family is gone from this phone (left, deleted): back to Settings.
    private void OnClosed(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await PopAsync());

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
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.Text(_model.Title, "Title"));
        if (_model.RefreshFailed)
            stack.Add(Ui.Text(FamilyPageViewModel.RefreshFailedText, "Meta", "Ink2"));

        stack.Add(Ui.Text(Strings.Get("Family.Members"), "Section"));
        foreach (var member in _model.Members)
        {
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
            grid.Add(new VerticalStackLayout
            {
                Spacing = 2,
                Children = { Ui.Text(member.Label, "BodyStrong"), Ui.Text(member.Email, "Meta", "Ink2"), Ui.Text(member.Role, "Meta", "Ink2") },
            }, 0);
            if (member.CanRemove)
                grid.Add(Styled(Strings.Get("Family.Remove"), "GhostButton", () => ConfirmAsync(() => Task.FromResult(_model.RemovePrompt(member)), () => _model.RemoveCommand.ExecuteAsync(member))), 1);
            stack.Add(new Card(grid));
        }

        if (_model.ShowsInvitations && _model.Invitations.Count > 0)
        {
            stack.Add(Ui.Text(Strings.Get("Family.Invitations"), "Section"));
            foreach (var invitation in _model.Invitations)
            {
                var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
                grid.Add(Ui.Text(invitation.Line, "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
                grid.Add(Styled(Strings.Get("Family.Withdraw"), "GhostButton", () => ConfirmAsync(() => Task.FromResult(_model.WithdrawPrompt), () => _model.WithdrawCommand.ExecuteAsync(invitation))), 1);
                stack.Add(new Card(grid));
            }
        }

        if (_model.ShowsInvite)
            stack.Add(Styled(Strings.Get("Family.Invite"), "PrimaryButton", () => _model.InviteCommand.ExecuteAsync(null)));
        if (_model.ShowsLeave)
            stack.Add(Styled(Strings.Get("Family.Leave"), "GhostButton", () => ConfirmAsync(_model.LeavePromptAsync, () => _model.LeaveCommand.ExecuteAsync(null))));

        // Ruling (Task 20): the delete prompt names what goes and is the only confirmation.
        if (_model.ShowsDelete)
            stack.Add(Styled(Strings.Get("Family.Delete"), "GhostButton", () => ConfirmAsync(_model.DeletePromptAsync, () => _model.DeleteCommand.ExecuteAsync(null))));
        _body.Content = stack;
    }

    /// <summary>One dialog at a time: a second tap while the first is being prepared or shown does nothing.</summary>
    private async Task ConfirmAsync(Func<Task<ConfirmPrompt>> prompt, Func<Task> action)
    {
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            var asked = await prompt();
            if (await DisplayAlertAsync(asked.Title, asked.Message, asked.Accept, asked.Cancel))
                await action();
        }
        finally
        {
            _confirming = false;
        }
    }

    private Button Styled(string text, string style, Func<Task> action)
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
                _undo.Say(Strings.Get("Error.Generic"));
            }
        };
        return button;
    }

    /// <summary>Only this page: popped when it is on top, removed when something was pushed over it, nothing when it is gone.</summary>
    private async Task PopAsync()
    {
        try
        {
            var stack = Navigation.NavigationStack;
            if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
                await Navigation.PopAsync();
            else if (stack.Contains(this))
                Navigation.RemovePage(this);
        }
#pragma warning disable CA1031 // Popping twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
