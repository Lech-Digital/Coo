using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.Pages;

/// <summary>
/// "Show which family?" as Coo's own sheet (walk fix F2): a card at the bottom over a dimmed Settings, one row per family with
/// the shown one checked and not tappable; a family that is not shown offers what <see cref="FamilySectionViewModel"/> allows.
/// Tapping outside, Back or Cancel closes it. It closes only itself; every handler catches everything.
/// </summary>
public sealed class FamilySwitcherPage : ContentPage
{
    private readonly SheetNavigator _sheets;
    private readonly Presentation.Today.UndoService _undo;
    private readonly VerticalStackLayout _rows = new() { Spacing = 8 };
    private FamilySectionViewModel? _model;
    private Func<FamilyRow, FamilyRowAction, Task>? _act;
    private bool _attached;
    private bool _busy;
    private bool _closeWanted;

    public FamilySwitcherPage(SheetNavigator sheets, Presentation.Today.UndoService undo)
    {
        (_sheets, _undo) = (sheets, undo);
        BackgroundColor = Color.FromRgba(0, 0, 0, 0.45);
        Shell.SetNavBarIsVisible(this, false);

        var scrim = new BoxView { Color = Colors.Transparent };
        scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Dispatcher.Dispatch(async () => await CloseAsync())) });

        var cancel = new Button { Text = FamilySectionViewModel.CancelText };
        cancel.SetDynamicResource(StyleProperty, "GhostButton");
        cancel.Clicked += async (_, _) => await CloseAsync();

        var card = Ui.Rounded(
            new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Spacing = 16,
                    Children = { Ui.Text(FamilySectionViewModel.SwitcherTitle, "Title"), _rows, cancel },
                },
            },
            "Ground", 28, new Thickness(24, 24, 24, 16));
        card.VerticalOptions = LayoutOptions.End;
        card.Margin = new Thickness(8, 0, 8, 8);

        // Taps on the card stay on the card: only the dimmed area around it closes the sheet.
        card.GestureRecognizers.Add(new TapGestureRecognizer());

        // The card wraps its rows, anchored at the bottom (device check): the dimmed area above takes the rest, and only a long
        // list scrolls inside the card, which never grows past three quarters of the screen.
        var root = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        root.Add(scrim, 0, 0);
        Grid.SetRowSpan(scrim, 2);
        root.Add(card, 0, 1);
        root.SizeChanged += (_, _) =>
        {
            if (root.Height > 0)
                card.MaximumHeightRequest = root.Height * 0.75;
        };
        Content = root;
    }

    /// <summary>The switcher over Settings: <paramref name="act"/> runs a row's action once the sheet has closed (Remove keeps it open).</summary>
    public static FamilySwitcherPage Open(IServiceProvider services, FamilySectionViewModel model, Func<FamilyRow, FamilyRowAction, Task> act)
    {
        ArgumentNullException.ThrowIfNull(services);
        var page = services.GetRequiredService<FamilySwitcherPage>();
        (page._model, page._act) = (model, act);
        return page;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // A close asked for while another sheet was over this one happens now that it is on top (review M2).
        if (_closeWanted)
        {
            Dispatcher.Dispatch(async () => await CloseAsync());
            return;
        }

        if (!_attached && _model is not null)
        {
            _attached = true;
            _model.PropertyChanged += OnModelChanged;
        }

        SafeRender();
    }

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

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    private void Detach()
    {
        if (!_attached || _model is null)
            return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilySectionViewModel.Rows))
            Dispatcher.Dispatch(SafeRender);
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            _rows.Clear();
            foreach (var row in _model?.Rows ?? [])
                _rows.Add(RowView(row));
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private View RowView(FamilyRow row)
    {
        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        top.Add(Ui.Text(row.Label, row.IsShown ? "BodyStrong" : "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        if (row.IsShown)
            top.Add(Ui.Text("✓", "BodyStrong", "AccentInk").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);

        var content = new VerticalStackLayout { Spacing = 10, Children = { top } };
        if (row.Actions.Count > 0)
        {
            var actions = new HorizontalStackLayout { Spacing = 20 };
            foreach (var action in row.Actions)
            {
                var link = Ui.Text(FamilySectionViewModel.ActionText(action), "Label", "AccentInk");
                link.Padding = new Thickness(0, 6);
                link.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Dispatcher.Dispatch(async () => await RunAsync(() => ActAsync(row, action)))) });
                actions.Add(link);
            }

            content.Add(actions);
        }

        var card = new Card(content);
        if (!row.IsShown)
            card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Dispatcher.Dispatch(async () => await RunAsync(() => ShowAsync(row)))) });
        return card;
    }

    /// <summary>The switch as before (§7.3): only the shown family changes; Today, Log and Settings follow.</summary>
    private async Task ShowAsync(FamilyRow row)
    {
        // Only once the sheet has really gone (review M3): with another sheet over it, nothing switches.
        if (_model is null || !await CloseAsync())
            return;
        await _model.SwitchCommand.ExecuteAsync(new FamilyChoice(row.Id, row.Label));
    }

    private async Task ActAsync(FamilyRow row, FamilyRowAction action)
    {
        if (_model is null || _act is null)
            return;
        if (action == FamilyRowAction.RemoveFromPhone)
        {
            var prompt = await _model.RemovePromptAsync(row);
            if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
                await _model.RemoveFromPhoneCommand.ExecuteAsync(row);
            return;
        }

        if (await CloseAsync())
            await _act(row, action);
    }

    /// <summary>One tap at a time: a second tap while a row's action or a confirmation runs does nothing.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await action();
        }
#pragma warning disable CA1031 // Tap handlers: nothing may escape; the view model shows its own failures.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast(Strings.Get("Error.Generic"));
        }
        finally
        {
            _busy = false;
        }
    }

    private void Toast(string text)
    {
#pragma warning disable CA1031 // A toast that cannot show must not crash the app.
        try
        {
            _undo.Say(text);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Only this page, never one over it (walk fix F1). True once it has gone (closed now, or already); covered, it closes once it
    /// is on top again and the caller does nothing more (review M2, M3).
    /// </summary>
    private async Task<bool> CloseAsync()
    {
        try
        {
            var closed = await _sheets.CloseAsync(this);
            _closeWanted = closed == SheetClose.Covered;
            return closed != SheetClose.Covered;
        }
#pragma warning disable CA1031 // Closing twice must not crash; a failed pop does not go on to the action.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
