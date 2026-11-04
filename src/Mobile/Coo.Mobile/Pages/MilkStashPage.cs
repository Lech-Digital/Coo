using Coo.Domain.CareLog.Kinds.MilkStash;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.MilkStash;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Pages;

/// <summary>
/// The milk stash: the fridge and freezer totals, every bag with the one to use first on top, what to do with each
/// (used, freeze, thaw, throw away), and adding a bag.
/// </summary>
public sealed class MilkStashPage : ContentPage
{
    private readonly MilkStashViewModel _model;
    private readonly ThemeController _theme;
    private readonly ContentView _body = new();
    private readonly ScrollView _scroll;
    private bool _renderQueued;

    public MilkStashPage(MilkStashViewModel model, ThemeController theme)
    {
        (_model, _theme) = (model, theme);
        Shell.SetNavBarIsVisible(this, false);
        _scroll = new ScrollView { Content = _body };
        Content = _scroll;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        await _model.LoadAsync();
        QueueRender();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Typing in the form changes these: redrawing would take the keyboard away.
        if (e.PropertyName is nameof(MilkStashViewModel.Amount) or nameof(MilkStashViewModel.Label)
            or nameof(MilkStashViewModel.ExpressedDate) or nameof(MilkStashViewModel.ExpressedTime))
            return;
        QueueRender();
    }

    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        var y = _scroll.ScrollY;
        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Kind.MilkBag"), () => Navigation.PopAsync()));
        if (!_model.IsReady)
        {
            _body.Content = stack;
            return;
        }

        if (_model.Totals.Length > 0)
            stack.Add(Ui.Text(_model.Totals, "Title"));
        if (_model.Warning.Length > 0)
            stack.Add(Ui.Rounded(Ui.Text(_model.Warning, "BodyStrong", "Accent"), "AccentSoft", 14, new Thickness(14, 10)));
        if (_model.Bags.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Stash.Empty"), "Body", "Ink2"));

        for (var i = 0; i < _model.Bags.Count; i++)
            stack.Add(BagCard(_model.Bags[i], first: i == 0));

        if (_model.Error.Length > 0)
            stack.Add(Ui.Text(_model.Error, "Body", "Accent"));
        if (_model.CanChange)
            stack.Add(AddCard());
        stack.Add(Ui.Text(Strings.Get("Stash.Guidance"), "Meta", "Ink2").Tap(l => l.FontSize = 11));

        _body.Content = stack;
        if (y > 0)
            Dispatcher.Dispatch(async () => await _scroll.ScrollToAsync(0, y, false));
    }

    private Card BagCard(MilkBagRow bag, bool first)
    {
        var look = EventAppearance.For("milkbag");
        var stack = new VerticalStackLayout { Spacing = 6 };
        var title = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        title.Add(Ui.Text(bag.Number.Length > 0 ? $"{bag.Number} · {bag.Amount} · {bag.Where}" : $"{bag.Amount} · {bag.Where}", "BodyStrong", look.Ink), 0);
        var tag = bag.Freshness switch
        {
            MilkFreshness.Expired => Strings.Get("Stash.ExpiredTag"),
            MilkFreshness.UseSoon => Strings.Get("Stash.SoonTag"),
            _ => first ? Strings.Get("Stash.UseFirst") : "",
        };
        if (tag.Length > 0)
            title.Add(Ui.Rounded(Ui.Text(tag, "Meta", bag.Freshness == MilkFreshness.Fine ? "Ink2" : "Accent"), "Surface", 999, new Thickness(10, 3), "Line"), 1);
        stack.Add(title);
        if (bag.Label is { } label)
            stack.Add(Ui.Text(label, "Body"));
        stack.Add(Ui.Text(bag.Dates, "Meta", "Ink2"));

        if (_model.CanChange && _model.Editing == bag.Id)
        {
            stack.Add(EditForm());
        }
        else if (_model.CanChange)
        {
            var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            if (bag.Freshness != MilkFreshness.Expired)
                buttons.Add(Button(Strings.Get("Stash.Use"), "PrimaryButton", () => _model.ChangeAsync(bag.Id, MilkBagChange.Use)));
            if (bag.Place == MilkPlace.Fridge && !bag.IsThawed && bag.Freshness != MilkFreshness.Expired)
                buttons.Add(Button(Strings.Get("Stash.Freeze"), "GhostButton", () => _model.ChangeAsync(bag.Id, MilkBagChange.Freeze)));
            if (bag.Place == MilkPlace.Freezer)
                buttons.Add(Button(Strings.Get("Stash.Thaw"), "GhostButton", () => _model.ChangeAsync(bag.Id, MilkBagChange.Thaw)));
            buttons.Add(Button(Strings.Get("Stash.Discard"), "GhostButton", () => _model.ChangeAsync(bag.Id, MilkBagChange.Discard)));
            buttons.Add(Button(Strings.Get("Stash.Edit"), "GhostButton", () =>
            {
                _model.StartEdit(bag.Id);
                return Task.CompletedTask;
            }));
            stack.Add(buttons);
        }

        return new Card(stack);
    }

    /// <summary>Inside the bag's card: how much is left, the number on the container, the note; Save or Cancel.</summary>
    private VerticalStackLayout EditForm()
    {
        var form = new VerticalStackLayout { Spacing = 8 };
        var amount = new Entry { Text = _model.EditAmount, Keyboard = Keyboard.Numeric, Placeholder = $"{Strings.Get("Stash.Left")} ({_model.UnitLabel})" };
        amount.TextChanged += (_, e) => _model.EditAmount = e.NewTextValue ?? "";
        var number = new Entry { Text = _model.EditNumber, Keyboard = Keyboard.Numeric, Placeholder = Strings.Get("Stash.Number"), MaxLength = 3 };
        number.TextChanged += (_, e) => _model.EditNumber = e.NewTextValue ?? "";
        var label = new Entry { Text = _model.EditLabel, Placeholder = Strings.Get("Stash.LabelHint"), MaxLength = MilkBagDetails.MaxLabelLength };
        label.TextChanged += (_, e) => _model.EditLabel = e.NewTextValue ?? "";
        form.Add(Ui.Text(Strings.Get("Stash.Left"), "Meta", "Ink2"));
        form.Add(amount);
        form.Add(Ui.Text(Strings.Get("Stash.Number"), "Meta", "Ink2"));
        form.Add(number);
        form.Add(label);
        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        buttons.Add(Button(Strings.Get("Stash.Save"), "PrimaryButton", _model.SaveEditAsync));
        buttons.Add(Button(Strings.Get("Action.Cancel"), "GhostButton", () =>
        {
            _model.CancelEdit();
            return Task.CompletedTask;
        }));
        form.Add(buttons);
        return form;
    }

    private Card AddCard()
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(Ui.Text(Strings.Get("Stash.Add"), "BodyStrong"));

        var amount = new Entry { Text = _model.Amount, Keyboard = Keyboard.Numeric, Placeholder = $"{Strings.Get("Stash.Amount")} ({_model.UnitLabel})" };
        amount.TextChanged += (_, e) => _model.Amount = e.NewTextValue ?? "";
        stack.Add(amount);

        var places = new HorizontalStackLayout { Spacing = 8 };
        foreach (var place in new[] { MilkPlace.Fridge, MilkPlace.Freezer })
        {
            var chosen = place;
            places.Add(Button(Strings.Get(place == MilkPlace.Fridge ? "Stash.Fridge" : "Stash.Freezer"), _model.Place == place ? "PrimaryButton" : "GhostButton",
                () =>
                {
                    _model.ChoosePlace(chosen);
                    return Task.CompletedTask;
                }));
        }

        stack.Add(places);

        stack.Add(Ui.Text(Strings.Get("Stash.Expressed"), "Meta", "Ink2"));
        var date = new DatePicker { Date = _model.ExpressedDate, MaximumDate = _model.Today };
        date.DateSelected += (_, e) => _model.ExpressedDate = e.NewDate ?? _model.ExpressedDate;
        var time = new TimePicker { Time = _model.ExpressedTime };
        time.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimePicker.Time) && time.Time is { } chosen)
                _model.ExpressedTime = chosen;
        };
        stack.Add(new HorizontalStackLayout { Spacing = 12, Children = { date, time } });

        var label = new Entry { Text = _model.Label, Placeholder = Strings.Get("Stash.LabelHint"), MaxLength = MilkBagDetails.MaxLabelLength };
        label.TextChanged += (_, e) => _model.Label = e.NewTextValue ?? "";
        stack.Add(label);

        var add = new Button { Text = Strings.Get("Stash.Add") };
        add.SetDynamicResource(StyleProperty, "PrimaryButton");
        add.Clicked += async (_, _) =>
        {
            if (await _model.AddAsync())
                await _scroll.ScrollToAsync(0, 0, true);
        };
        stack.Add(add);
        return new Card(stack);
    }

    private static Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text, Margin = new Thickness(0, 4, 8, 0) };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await action();
        return button;
    }
}
