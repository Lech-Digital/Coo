using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// Settings → Family → Bring entries over (spec §6.3 "Later"): the shown family into another account family, chosen when
/// there are several; a never-synced source moves, a synced one copies; a family of another account is never offered.
/// A modal page opened from a tap in Settings; it closes only itself.
/// </summary>
public sealed class FamilyBringOverPage : ContentPage
{
    private readonly FamilyBringOverViewModel _model;
    private readonly Families _families;
    private readonly BringFamilyEntriesOver _bring;
    private readonly ISyncControl _sync;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private bool _attached;
    private bool _started;
    private bool _closePending;

    public FamilyBringOverPage(FamilyBringOverViewModel model, Families families, BringFamilyEntriesOver bring, ISyncControl sync, UndoService undo)
    {
        (_model, _families, _bring, _sync, _undo) = (model, families, bring, sync, undo);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            Attach();

            // A close asked for while a sheet was over this page happens now that it is on top again.
            if (_closePending)
            {
                Dispatcher.Dispatch(async () => await CloseAsync());
                return;
            }

            if (_started)
            {
                SafeRender();
                return;
            }

            _started = true;
            if (await _families.ShownAsync(CancellationToken.None) is not { MayBeBroughtOver: true } source)
            {
                await CloseAsync();
                return;
            }

            var targets = await _families.BringOverTargetsAsync(source.Id, CancellationToken.None);
            var target = targets.Count == 1 ? targets[0] : await ChooseAsync(targets);
            if (target is null)
            {
                await CloseAsync();
                return;
            }

            var choice = await _bring.PrepareAsync(source.Id, target.Id, CancellationToken.None);
            var name = target.Membership?.OwnerName ?? "";
            _model.Start(new BringOverRequest(
                source.Id, choice,
                choice.Family.Count == 0 ? Strings.Get("Join.NoBaby") : Strings.Format("Join.Babies", string.Join(", ", choice.Family.Select(b => b.Name))),
                name,
                async (mapping, ct) =>
                {
                    var outcome = await _bring.ExecuteAsync(source.Id, target.Id, mapping, ct);
                    _sync.NotifyDataChanged();
                    _sync.RequestSync();
                    return outcome;
                },
                Keep: null,
                Strings.Format("Join.Copied", name)));
            SafeRender();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Disappearing is also raised by a sheet opening over this one while the bring-over may still be running: the page
    /// stops listening once it has left the modal stack.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Dispatcher.Dispatch(() =>
        {
#pragma warning disable CA1031 // A dispatched check: nothing may escape.
            try
            {
                if (!ModalStack().Contains(this))
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
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    private void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _model.PropertyChanged += OnModelChanged;
        _model.Finished += OnFinished;
    }

    private void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
        _model.Finished -= OnFinished;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(SafeRender);

    private void OnFinished(object? sender, string toast) => Dispatcher.Dispatch(async () =>
    {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape (controller carry F5).
        try
        {
            await CloseAsync();
            if (toast.Length > 0)
                _undo.Say(toast);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    });

    private async Task<FamilyOnPhone?> ChooseAsync(IReadOnlyList<FamilyOnPhone> targets)
    {
        if (targets.Count == 0)
            return null;
        var labels = targets.Select(FamilyLabels.Choice).ToList();
        var chosen = await DisplayActionSheetAsync(Strings.Get("BringOver.ChooseTarget"), Strings.Get("Action.Cancel"), null, [.. labels]);
        var index = labels.IndexOf(chosen ?? "");
        return index >= 0 ? targets[index] : null;
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            _body.Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 48, 24, 32),
                Children = { BringOverViews.Render(_model, RunAsync) },
            };
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
#pragma warning disable CA1031 // async void handlers: nothing may escape.
        catch (Exception)
#pragma warning restore CA1031
        {
            _undo.Say(Strings.Get("Error.Generic"));
        }
    }

    private static IReadOnlyList<Page> ModalStack() =>
        Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } root ? root.Navigation.ModalStack : [];

    /// <summary>Only this page (Task 23's M1): popped when it is on top; with a sheet over it, once it is on top again.</summary>
    private async Task CloseAsync()
    {
        try
        {
            var stack = ModalStack();
            if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            {
                _closePending = false;
                await Navigation.PopModalAsync();
            }
            else
            {
                _closePending = stack.Contains(this);
            }
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
