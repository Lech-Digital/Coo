using System.Collections.Specialized;
using System.ComponentModel;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Today (Spec 006 §4–§6): the sections for the current stage and palette, the sheets, and the toast.</summary>
public sealed class TodayPage : ContentPage
{
    private readonly TodayViewModel _model;
    private readonly IServiceProvider _services;
    private readonly VerticalStackLayout _sections = new() { Padding = new Thickness(0, 0, 0, 24) };
    private readonly Sheet _sheet = new();
    private readonly UndoService _undo;
    private readonly LanguageController _language;
    private IDispatcherTimer? _tick;
    private bool _pageSheet;
    private bool _rebuildQueued;
    private IReadOnlyList<IHomeSection> _built = [];
    private string? _builtLanguage;

    public TodayPage(TodayViewModel model, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;
        _undo = undo;
        _language = language;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);

        // "Earlier…" is the page's own sheet, not the view model's, so a tap outside hides it here. Closing it through
        // the view model left it open and forgot it, and Back then sent Coo to the background (owner, Pixel 10).
        _sheet.Closed += async (_, _) =>
        {
            if (_pageSheet)
            {
                _pageSheet = false;
                await _sheet.HideAsync();
                return;
            }

            model.CloseSheetCommand.Execute(null);
        };


        var root = new Grid();
        root.Add(new ScrollView { Content = _sections });
        root.Add(new UndoToastView(undo));
        root.Add(_sheet);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Listening only while on screen: the view model outlives this page (Spec 009, owner's finding on the Pixel 10).
        _undo.DataChanged += OnDataChanged;
        _model.Sections.CollectionChanged += OnSectionsChanged;
        _model.PropertyChanged += OnModelChanged;
        _language.Changed += OnLanguageChanged;
        await _model.RefreshAsync();

        // The section views update themselves; they are rebuilt only when the sections or the language changed.
        if (!_built.SequenceEqual(_model.Sections) || _builtLanguage != Strings.Language)
            QueueRebuild();
        _tick ??= Dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += OnTick;
        _tick.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _undo.DataChanged -= OnDataChanged;
        _model.Sections.CollectionChanged -= OnSectionsChanged;
        _model.PropertyChanged -= OnModelChanged;
        _language.Changed -= OnLanguageChanged;
        if (_tick is not null)
        {
            _tick.Stop();
            _tick.Tick -= OnTick;
        }
    }

    /// <summary>Back closes an open sheet before it leaves the page (Spec 006 §2).</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_pageSheet)
        {
            _ = _sheet.HideAsync();
            _pageSheet = false;
            return true;
        }

        if (_model.Sheet == HomeSheet.None)
            return base.OnBackButtonPressed();
        _model.CloseSheetCommand.Execute(null);
        return true;
    }

    // After an undo, or a delete that the window commits, the sections show what is now stored.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await _model.RefreshAsync());

    private void OnTick(object? sender, EventArgs e) => _model.Tick();

    private async void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodayViewModel.Sheet))
            await ShowSheetAsync();
        else if (e.PropertyName == nameof(TodayViewModel.Toast))
            ShowToast(_model.Toast);
    }

    // A palette change clears and refills the sections: seven events, one rebuild.
    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await _model.RefreshAsync();
        QueueRebuild();
    });

    private void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        foreach (var old in _sections.Children)
            LiveView.Release(old as IVisualTreeElement);
        _sections.Children.Clear();
        foreach (var section in _model.Sections)
            _sections.Add(SectionViews.Create(section, new SectionHost(OpenDebug, OpenEntry, OpenAttention, OpenEarlier, _model)));
        _built = [.. _model.Sections];
        _builtLanguage = Strings.Language;
    }

    private async void OpenEntry(EntryId id)
    {
        var page = _services.GetRequiredService<EditPage>();
        await page.OpenAsync(id);
        await Navigation.PushAsync(page);
    }

    private async void OpenAttention() => await Navigation.PushAsync(_services.GetRequiredService<AttentionPage>());

    /// <summary>"Earlier…" below the tiles: which kind, then the form (Spec 007 §4).</summary>
    private async void OpenEarlier()
    {
        _pageSheet = true;
        // Hidden kinds offer no "Earlier…" choice (Spec 009 §3.2).
        var rows = new (string Kind, View Button)[]
        {
            ("feeding", EarlierButton("feeding", "Kind.Feed")),
            ("feeding", EarlierButton("feeding", "Feed.Bottle", bottle: true)),
            ("diaper", EarlierButton("diaper", "Kind.Diaper")),
            ("sleep", EarlierButton("sleep", "Kind.Sleep")),
            ("pump", EarlierButton("pump", "Kind.Pump")),
        }.Where(b => _model.IsShown(b.Kind)).Select(b => b.Button).Chunk(2).Select(pair => (View)Row(pair)).ToArray();
        await _sheet.ShowAsync(Panel(Strings.Get("Edit.Earlier"), rows));
    }

    private Button EarlierButton(string kindCode, string key, bool bottle = false)
    {
        var button = Big(Strings.Get(key), new Command(async () => await OpenNewAsync(kindCode, bottle)), kindCode, kindCode);
        return button;
    }

    private View EarlierLink(string kindCode, bool bottle = false)
    {
        var link = Ui.Text(Strings.Get("Edit.Earlier"), "Label", "Ink2");
        link.Padding = new Thickness(0, 8);
        link.HorizontalOptions = LayoutOptions.End;
        link.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenNewAsync(kindCode, bottle)) });
        return link;
    }

    private async Task OpenNewAsync(string kindCode, bool bottle)
    {
        _pageSheet = false;
        _model.CloseSheetCommand.Execute(null);
        await _sheet.HideAsync();
        var page = _services.GetRequiredService<EditPage>();
        await page.NewAsync(kindCode, bottle);
        await Navigation.PushAsync(page);
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
        Ghost(Strings.Get("Feed.Bottle"), _model.OpenBottleCommand),
        EarlierLink("feeding"));

    private View DiaperSheet() => Panel(
        Strings.Get("Kind.Diaper"),
        Row(
            Big(Strings.Get("Diaper.Wet"), _model.LogDiaperCommand, DiaperContents.Wet, "diaper"),
            Big(Strings.Get("Diaper.Dirty"), _model.LogDiaperCommand, DiaperContents.Dirty, "diaper"),
            Big(Strings.Get("Diaper.Mixed"), _model.LogDiaperCommand, DiaperContents.Mixed, "diaper")),
        EarlierLink("diaper"));

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
        children.Add(EarlierLink("feeding", bottle: true));
        return Panel(Strings.Get("Feed.Bottle"), [.. children]);
    }

    private View PumpSheet()
    {
        View Amount(string placeholder, string property)
        {
            var entry = new Entry { Placeholder = $"{placeholder} ({_model.UnitLabel})", Keyboard = Keyboard.Numeric, FontSize = 17, FontFamily = "FigtreeRegular" };
            entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
            entry.BindingContext = _model;
            entry.SetBinding(Entry.TextProperty, property);
            return Ui.Rounded(entry, "Surface", 12, new Thickness(16, 4), "Line");
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
        return Panel(Strings.Get("Due.Arrived"), Ui.Text(Strings.Get("Setup.BirthDate"), "Label"), Ui.Rounded(picker, "Surface", 12, new Thickness(16, 4), "Line"), Primary(Strings.Get("Action.Save"), _model.SaveBirthCommand));
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

    /// <summary>Plain messages ("Switch side") go through the one toast; logs with Undo arrive from the service.</summary>
    private void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _undo.Say(text);
        _model.Toast = "";
    }
}
