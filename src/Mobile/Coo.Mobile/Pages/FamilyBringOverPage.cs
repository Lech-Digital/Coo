using Coo.Mobile.Components;
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
/// A modal page opened from a tap in Settings; it closes only itself. From the family switcher (walk fix F2) it brings a family
/// that is not shown into the shown one (<see cref="Into"/>), under the same rules.
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
    private (Coo.Domain.Common.HouseholdId Source, Coo.Domain.Common.HouseholdId Target)? _preset;

    public FamilyBringOverPage(FamilyBringOverViewModel model, Families families, BringFamilyEntriesOver bring, ISyncControl sync, UndoService undo, SheetNavigator sheets)
    {
        (_model, _families, _bring, _sync, _undo, _sheets) = (model, families, bring, sync, undo, sheets);
        Shell.SetNavBarIsVisible(this, false);
        Content = Backdrop.Behind(new ScrollView { Content = _body });
    }

    /// <summary>The family switcher's "Bring its entries here": <paramref name="source"/> into <paramref name="target"/>, the shown family.</summary>
    public static FamilyBringOverPage Into(IServiceProvider services, Coo.Domain.Common.HouseholdId source, Coo.Domain.Common.HouseholdId target)
    {
        ArgumentNullException.ThrowIfNull(services);
        var page = services.GetRequiredService<FamilyBringOverPage>();
        page._preset = (source, target);
        return page;
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
            if (await PickAsync() is not ({ } source, { } target))
            {
                await CloseAsync();
                return;
            }

            (_source, _target) = (source, target.Id);
            var choice = await _bring.PrepareAsync(source, target.Id, CancellationToken.None);
            _model.Start(BringOverRequest.ForSettings(source, choice, target, async (mapping, ct) =>
            {
                // BringFamilyEntriesOver runs its move or copy in the exclusive section itself (final review C1).
                var outcome = await _bring.ExecuteAsync(source, target.Id, mapping, ct);
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

    /// <summary>
    /// The source and the target: from Settings the shown family into the one account family offered (or the one chosen); from
    /// the switcher the family it named into the shown family, if §6.3 still allows it. Nothing when the flow does not fit.
    /// </summary>
    private async Task<(Coo.Domain.Common.HouseholdId? Source, FamilyOnPhone? Target)> PickAsync()
    {
        if (_preset is { } preset)
        {
            var list = await _families.ListAsync(CancellationToken.None);
            if (list.FirstOrDefault(f => f.Id == preset.Source) is not { MayBeBroughtOver: true }
                || list.FirstOrDefault(f => f.Id == preset.Target) is not { IsShown: true }
                || (await _families.BringOverTargetsAsync(preset.Source, CancellationToken.None)).FirstOrDefault(f => f.Id == preset.Target) is not { } into)
                return (null, null);
            return (preset.Source, into);
        }

        if (await _families.ShownAsync(CancellationToken.None) is not { MayBeBroughtOver: true } source)
            return (null, null);
        var targets = await _families.BringOverTargetsAsync(source.Id, CancellationToken.None);
        var target = targets.Count == 1 ? targets[0] : await ChooseAsync(targets);
        return target is null ? (null, null) : (source.Id, target);
    }

    /// <summary>
    /// Whether the flow still fits the phone: the source may be brought over and the target is still offered; from Settings the
    /// source is still the shown family, from the switcher the target is.
    /// </summary>
    private async Task<bool> StillValidAsync()
    {
        // Not started yet (an action sheet open, say): nothing to check.
        if (_source is not { } source || _target is not { } target)
            return true;
        var list = await _families.ListAsync(CancellationToken.None);
        var shownId = list.FirstOrDefault(f => f.IsShown)?.Id;
        if (list.FirstOrDefault(f => f.Id == source) is not { MayBeBroughtOver: true } || shownId != (_preset is null ? source : target))
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
