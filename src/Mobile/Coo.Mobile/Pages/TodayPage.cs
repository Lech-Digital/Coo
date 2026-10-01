using System.Collections.Specialized;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Today (Spec 006 §4–§6): the sections for the current stage and palette, the sheets, and the toast.</summary>
public sealed class TodayPage : ContentPage
{
    private readonly TodayViewModel _model;
    private readonly IServiceProvider _services;
    private readonly VerticalStackLayout _sections = new() { Padding = new Thickness(0, 24, 0, 24) };
    private readonly Sheet _sheet = new();
    private readonly Label _toastText = Ui.Text("", "BodyStrong", "VoiceInk");
    private readonly Border _toast;
    private IDispatcherTimer? _tick;
    private int _toastVersion;

    public TodayPage(TodayViewModel model, IServiceProvider services)
    {
        _model = model;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);

        // The page pads its bottom for the keyboard (MAUI 10's SoftInput region), so a sheet with fields keeps them
        // and its Save button above the keyboard. Found on the phone: without it, the second field and Save were hidden.
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.SoftInput);
        _toast = Ui.Rounded(_toastText, "VoiceBg", 999, new Thickness(20, 14));
        _toast.Margin = new Thickness(24, 0, 24, 16);
        _toast.VerticalOptions = LayoutOptions.End;
        _toast.IsVisible = false;

        model.Sections.CollectionChanged += OnSectionsChanged;
        model.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(TodayViewModel.Sheet))
                await ShowSheetAsync();
            else if (e.PropertyName == nameof(TodayViewModel.Toast))
                ShowToast(model.Toast);
        };
        _sheet.Closed += (_, _) => model.CloseSheetCommand.Execute(null);

        var root = new Grid();
        root.Add(new ScrollView { Content = _sections });
        root.Add(_toast);
        root.Add(_sheet);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.RefreshAsync();
        Rebuild();
        _tick ??= Dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += OnTick;
        _tick.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_tick is not null)
        {
            _tick.Stop();
            _tick.Tick -= OnTick;
        }
    }

    /// <summary>Back closes an open sheet before it leaves the page (Spec 006 §2).</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_model.Sheet == HomeSheet.None)
            return base.OnBackButtonPressed();
        _model.CloseSheetCommand.Execute(null);
        return true;
    }

    private void OnTick(object? sender, EventArgs e) => _model.Tick();

    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.Dispatch(Rebuild);

    private void Rebuild()
    {
        _sections.Children.Clear();
        foreach (var section in _model.Sections)
            _sections.Add(SectionViews.Create(section, OpenDebug));
    }

    private void OpenDebug()
    {
#if DEBUG_SCREEN
        Navigation.PushAsync(_services.GetRequiredService<DebugScreen.DebugPage>());
#endif
    }

    private async Task ShowSheetAsync()
    {
        View? content = _model.Sheet switch
        {
            HomeSheet.Feed => FeedSheet(),
            HomeSheet.Bottle => BottleSheet(),
            HomeSheet.Diaper => DiaperSheet(),
            HomeSheet.PumpYield => PumpSheet(),
            HomeSheet.RecordBirth => BirthSheet(),
            HomeSheet.AwakeCheck => AwakeSheet(),
            _ => null,
        };

        if (content is null)
            await _sheet.HideAsync();
        else
            await _sheet.ShowAsync(content);
    }

    private View FeedSheet() => Panel(
        Strings.Get("Kind.Feed"),
        Row(
            Big(Strings.Get("Feed.Left"), _model.StartBreastCommand, BreastSide.Left, "feeding"),
            Big(Strings.Get("Feed.Right"), _model.StartBreastCommand, BreastSide.Right, "feeding")),
        Ui.Text(Strings.Get("Feed.BreastHint"), "Meta"),
        Ghost(Strings.Get("Feed.Bottle"), _model.OpenBottleCommand));

    private View DiaperSheet() => Panel(
        Strings.Get("Kind.Diaper"),
        Row(
            Big(Strings.Get("Diaper.Wet"), _model.LogDiaperCommand, DiaperContents.Wet, "diaper"),
            Big(Strings.Get("Diaper.Dirty"), _model.LogDiaperCommand, DiaperContents.Dirty, "diaper"),
            Big(Strings.Get("Diaper.Mixed"), _model.LogDiaperCommand, DiaperContents.Mixed, "diaper")));

    private View BottleSheet()
    {
        var milk = new HorizontalStackLayout { Spacing = 8 };
        foreach (var (choice, key) in new[] { (BottleMilk.Formula, "Milk.Formula"), (BottleMilk.Breast, "Milk.Breast"), (BottleMilk.Both, "Milk.Both") })
        {
            var button = new Button { Text = Strings.Get(key) };
            button.SetDynamicResource(VisualElement.StyleProperty, _model.Milk == choice ? "PrimaryButton" : "GhostButton");
            button.Clicked += async (_, _) =>
            {
                _model.Milk = choice;
                await _sheet.ShowAsync(BottleSheet());
            };
            milk.Add(button);
        }

        var children = new List<View> { milk, Stepper(() => _model.BottleAmount, _model.ChangeAmountCommand, _model.Milk == BottleMilk.Both ? Strings.Get("Milk.Breast") : null) };
        if (_model.Milk == BottleMilk.Both)
            children.Add(Stepper(() => _model.SecondAmount, _model.ChangeSecondAmountCommand, Strings.Get("Milk.Formula")));
        children.Add(Primary(Strings.Get("Action.LogBottle"), _model.LogBottleCommand));
        return Panel(Strings.Get("Feed.Bottle"), [.. children]);
    }

    private View PumpSheet()
    {
        Entry Amount(string placeholder, string property)
        {
            var entry = new Entry { Placeholder = $"{placeholder} ({_model.UnitLabel})", Keyboard = Keyboard.Numeric, FontSize = 17, FontFamily = "FigtreeRegular" };
            entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
            entry.BindingContext = _model;
            entry.SetBinding(Entry.TextProperty, property);
            return entry;
        }

        return Panel(
            Strings.Get("Pump.HowMuch"),
            Amount(Strings.Get("Feed.Left"), nameof(TodayViewModel.PumpLeft)),
            Amount(Strings.Get("Feed.Right"), nameof(TodayViewModel.PumpRight)),
            Row(Ghost(Strings.Get("Action.Skip"), _model.SavePumpYieldCommand, true), Primary(Strings.Get("Action.Save"), _model.SavePumpYieldCommand, false)));
    }

    /// <summary>"Zosia is asleep · since 20:05": woke up, or still asleep (Spec 006 §5a). Tap outside cancels.</summary>
    private View AwakeSheet() => Panel(
        _model.AwakeTitle,
        Ui.Text(_model.AwakeSince, "Body", "Ink2"),
        Row(Ghost(Strings.Get("Awake.StillAsleep"), _model.ResolveAwakeCommand, false), Primary(Strings.Get("Awake.WokeUp"), _model.ResolveAwakeCommand, true)),
        Ui.Text(Strings.Get("Awake.Hint"), "Meta"));

    private View BirthSheet()
    {
        var picker = new DatePicker { MaximumDate = _model.Today, Date = _model.BirthDate, FontSize = 17, FontFamily = "FigtreeRegular", Format = "D" };
        picker.SetDynamicResource(DatePicker.TextColorProperty, "Ink");
        picker.DateSelected += (_, e) => _model.BirthDate = e.NewDate ?? _model.Today;
        return Panel(Strings.Get("Due.Arrived"), Ui.Text(Strings.Get("Setup.BirthDate"), "Label"), picker, Primary(Strings.Get("Action.Save"), _model.SaveBirthCommand));
    }

    private View Stepper(Func<decimal> value, System.Windows.Input.ICommand change, string? label)
    {
        var number = Ui.Text($"{value():0.#} {_model.UnitLabel}", "Window");
        number.HorizontalOptions = LayoutOptions.Center;
        number.VerticalOptions = LayoutOptions.Center;
        Button Sign(string sign)
        {
            var b = new Button { Text = sign == "+" ? "+" : "−", WidthRequest = 64, FontSize = 24 };
            b.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
            b.Clicked += (_, _) =>
            {
                change.Execute(sign);
                number.Text = $"{value():0.#} {_model.UnitLabel}";
            };
            return b;
        }

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        grid.Add(Sign("-"), 0);
        grid.Add(number, 1);
        grid.Add(Sign("+"), 2);
        return label is null ? grid : new VerticalStackLayout { Spacing = 4, Children = { Ui.Text(label, "Label"), grid } };
    }

    private static VerticalStackLayout Panel(string title, params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 14 };
        stack.Add(Ui.Text(title, "Section"));
        foreach (var child in children)
            stack.Add(child);
        return stack;
    }

    private static Grid Row(params View[] views)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < views.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(views[i], i);
        }

        return grid;
    }

    private static Button Big(string text, System.Windows.Input.ICommand command, object parameter, string kindCode)
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For(kindCode);
        var button = new Button { Text = text, Command = command, CommandParameter = parameter, MinimumHeightRequest = 68, CornerRadius = 20, FontFamily = "FigtreeSemiBold", FontSize = 17 };
        button.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);
        button.SetDynamicResource(Button.TextColorProperty, look.Ink);
        return button;
    }

    private static Button Primary(string text, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter };
        button.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        return button;
    }

    private static Button Ghost(string text, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter };
        button.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        return button;
    }

    private async void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Each toast shows for 3 seconds; a newer one replaces it, and the older timer then does nothing.
        var version = ++_toastVersion;
        _toastText.Text = text;
        _toast.IsVisible = true;
        await Task.Delay(3000);
        if (version != _toastVersion)
            return;
        _toast.IsVisible = false;
        _model.Toast = "";
    }
}
