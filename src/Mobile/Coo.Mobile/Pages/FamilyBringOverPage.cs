using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Sheets;
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
    private readonly SheetNavigator _sheets;
    private readonly ContentView _body = new();
    private bool _attached;
    private bool _started;
    private bool _closePending;
    private Coo.Domain.Common.HouseholdId? _source;
    private Coo.Domain.Common.HouseholdId? _target;

    public FamilyBringOverPage(FamilyBringOverViewModel model, Families families, BringFamilyEntriesOver bring, ISyncControl sync, UndoService undo, SheetNavigator sheets)
    {
        (_model, _families, _bring, _sync, _undo, _sheets) = (model, families, bring, sync, undo, sheets);
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
                // Back from a sheet: the shown family or the target may have changed meanwhile (a switch, a removal). Only
                // before anything was brought over; the old-family question after a copy stands on its own.
                if (!_model.IsBusy && _model.Step != BringOverStep.OldFamily && !await StillValidAsync())
                {
                    await CloseAsync();
                    return;
                }

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

            (_source, _target) = (source.Id, target.Id);
            var choice = await _bring.PrepareAsync(source.Id, target.Id, CancellationToken.None);
            _model.Start(BringOverRequest.ForSettings(source.Id, choice, target, async (mapping, ct) =>
            {
                // BringFamilyEntriesOver runs its move or copy in the exclusive section itself (final review C1).
                var outcome = await _bring.ExecuteAsync(source.Id, target.Id, mapping, ct);
                _sync.NotifyDataChanged();
                _sync.RequestSync();
                return outcome;
            }));
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
                if (!_sheets.Contains(this))
                    Detach();
            }
            catch (Exception)
            {
                Detach();
            }
#pragma warning restore CA1031
        });
    }

    /// <summary>Back cancels, but not while a copy or a leave runs: its outcome closes the page.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (!_model.IsBusy)
            Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    /// <summary>Whether the flow still fits the phone: the source is shown and may be brought over, and the target is still offered.</summary>
    private async Task<bool> StillValidAsync()
    {
        // Not started yet (an action sheet open, say): nothing to check.
        if (_source is not { } source || _target is not { } target)
            return true;
        if (await _families.ShownAsync(CancellationToken.None) is not { MayBeBroughtOver: true } shown || shown.Id != source)
            return false;
        return (await _families.BringOverTargetsAsync(source, CancellationToken.None)).Any(f => f.Id == target);
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
        var index = await ActionSheetChoices.ChooseIndexAsync(this, Strings.Get("BringOver.ChooseTarget"), Strings.Get("Action.Cancel"), [.. targets.Select(FamilyLabels.Choice)]);
        return index >= 0 && index < targets.Count ? targets[index] : null;
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

    /// <summary>Only this page (Task 23's M1, walk fix F1): popped when it is on top; with a sheet over it, once it is on top again.</summary>
    private async Task CloseAsync()
    {
        try
        {
            _closePending = await _sheets.CloseAsync(this) == SheetClose.Covered;
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
