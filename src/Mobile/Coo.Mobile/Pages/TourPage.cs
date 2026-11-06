using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Tour;

namespace Coo.Mobile.Pages;

/// <summary>
/// "How Mochi works": one tip per screen: a picture, the title, where it is in the app, and its points as a short list. "Skip" at the top; "Previous tip" and "Next tip" at the bottom, and a swipe does
/// the same. Opened over the welcome screen at the first start, and from Settings at any time.
/// </summary>
public sealed class TourPage : ContentPage
{
    private readonly TourViewModel _model;
    private readonly ThemeController _theme;
    /// <summary>
    /// The picture, the title and the text sit at the same place on every tip: the picture in a box of fixed height, the
    /// title in two lines' room, the text from a fixed line down, as long as it needs (scrolling if a language needs
    /// more). Centred as one block, a tip whose text wrapped differently moved the icon (owner, 2026-11-03).
    /// </summary>
    private const double ArtSize = 136;

    private readonly ContentView _art = new() { HeightRequest = ArtSize, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _title = Ui.Text("", "Title");
    private readonly ContentView _where = new() { HorizontalOptions = LayoutOptions.Center };
    private readonly VerticalStackLayout _points = new() { Spacing = 12, HorizontalOptions = LayoutOptions.Fill, Padding = new Thickness(8, 0) };
    private readonly Label _position = Ui.Text("", "Meta", "Ink2");
    private readonly HorizontalStackLayout _dots = new() { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
    private readonly Button _previous = new() { Text = Strings.Get("Tour.Previous") };
    private readonly Button _next = new();

    public TourPage(TourViewModel model, ThemeController theme)
    {
        (_model, _theme) = (model, theme);
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        // From Settings the tour is a page in the tabs: the tab bar would sit under its buttons.
        Shell.SetTabBarIsVisible(this, false);
        SetDynamicResource(BackgroundColorProperty, "Ground");

        var skip = Ui.Text(Strings.Get("Tour.Skip"), "BodyStrong", "Accent");
        skip.HorizontalOptions = LayoutOptions.End;
        skip.Padding = new Thickness(12, 8);
        skip.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.SkipCommand });
        SemanticProperties.SetDescription(skip, Strings.Get("Tour.Skip"));
        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        _position.VerticalOptions = LayoutOptions.Center;
        top.Add(_position, 0);
        top.Add(skip, 1);

        _previous.SetDynamicResource(StyleProperty, "GhostButton");
        _previous.Command = model.PreviousCommand;
        _next.SetDynamicResource(StyleProperty, "PrimaryButton");
        _next.Command = model.NextCommand;
        var bottom = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 12 };
        bottom.Add(_previous, 0);
        bottom.Add(_next, 1);

        // A swipe moves between tips as the buttons do.
        var swipeLeft = new SwipeGestureRecognizer { Direction = SwipeDirection.Left, Command = model.NextCommand };
        var swipeRight = new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Command = model.PreviousCommand };
        _title.HorizontalTextAlignment = TextAlignment.Center;
        _title.VerticalTextAlignment = TextAlignment.Start;
        _title.MaxLines = 2;
        _title.LineBreakMode = LineBreakMode.WordWrap;
        // Two lines of the 28-point title: a title that wraps ("Pieluchy, kąpiele i kamienie milowe") moves nothing.
        _title.HeightRequest = 2 * 28 * 1.3;

        // The free space above the picture is a third of what is left, so the picture sits a little above the middle;
        // the text below gets the rest. Only the fixed-size rows decide where things are, never the text's length.
        var middle = new Grid
        {
            RowDefinitions = [new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(new GridLength(3, GridUnitType.Star))],
            RowSpacing = 16,
        };
        middle.Add(_art, 0, 1);
        middle.Add(_title, 0, 2);
        // Where it is, in a fixed-height row, so the points start at the same place on every tip.
        var whereRow = new Grid { HeightRequest = 32, Children = { _where } };
        middle.Add(whereRow, 0, 3);
        middle.Add(new ScrollView { Content = _points, VerticalOptions = LayoutOptions.Fill }, 0, 4);
        var tipArea = new Grid { BackgroundColor = Colors.Transparent, Children = { middle } };
        tipArea.GestureRecognizers.Add(swipeLeft);
        tipArea.GestureRecognizers.Add(swipeRight);

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            RowSpacing = 20,
            Padding = new Thickness(24, 24, 24, 32),
        };
        root.Add(top, 0, 0);
        root.Add(tipArea, 0, 1);
        root.Add(_dots, 0, 2);
        root.Add(bottom, 0, 3);
        Content = root;

        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(Render);
        model.Finished += async (_, _) => await CloseAsync();
        Render();
    }

    /// <summary>Back goes to the tip before; on the first tip it leaves the tour, as Skip does.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_model.CanGoBack)
            _model.PreviousCommand.Execute(null);
        else
            _model.SkipCommand.Execute(null);
        return true;
    }

    private async Task CloseAsync()
    {
        if (Navigation.ModalStack.Contains(this))
            await Navigation.PopModalAsync();
        else if (Navigation.NavigationStack.Contains(this))
            await Navigation.PopAsync();
    }

    private void Render()
    {
        var step = _model.Step;
        var look = EventAppearance.For(step.KindCode);
        View art;
        if (step.Icon is { } icon)
        {
            var circle = Ui.Rounded(new Icon(icon, look.Ink, 56).Tap(i => i.HorizontalOptions = LayoutOptions.Center), look.Tint, 999, new Thickness(32));
            circle.WidthRequest = circle.HeightRequest = ArtSize;
            circle.HorizontalOptions = LayoutOptions.Center;
            art = circle;
        }
        else
        {
            // The welcome: Mochi itself, and the name under the title's place.
            var mochi = new Image { Source = "mochi_awake.png", WidthRequest = ArtSize, HeightRequest = ArtSize, HorizontalOptions = LayoutOptions.Center };
            AutomationProperties.SetIsInAccessibleTree(mochi, false);
            art = mochi;
        }

        _art.Content = art;
        _title.Text = _model.Title;

        // "Today · Next up": where to find it, as a soft pill; nothing for the welcome.
        if (_model.Where.Length > 0)
        {
            var pill = Ui.Rounded(
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children =
                    {
                        new Icon(IconGeometry.ChevronRight, "AccentInk", 14) { VerticalOptions = LayoutOptions.Center },
                        Ui.Text(_model.Where, "BodyStrong", "AccentInk").Tap(l => { l.FontSize = 14; l.VerticalOptions = LayoutOptions.Center; }),
                    },
                },
                "AccentSoft",
                999,
                new Thickness(12, 5));
            pill.HorizontalOptions = LayoutOptions.Center;
            _where.Content = pill;
        }
        else
        {
            _where.Content = null;
        }

        // The points as a short list, each with a dot in the tip's colour: easier to read than a centred paragraph.
        _points.Children.Clear();
        foreach (var point in _model.Points)
        {
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
            var dot = new BoxView { WidthRequest = 8, HeightRequest = 8, CornerRadius = 4, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 8, 0, 0) };
            dot.SetDynamicResource(BoxView.ColorProperty, look.Mark);
            row.Add(dot, 0);
            row.Add(Ui.Text(point, "Body", "Ink").Tap(l => l.LineHeight = 1.15), 1);
            _points.Children.Add(row);
        }
        SemanticProperties.SetDescription(_art, $"{_model.Title}. {_model.Where}. {string.Join(" ", _model.Points)}");

        _position.Text = _model.Position;
        _dots.Children.Clear();
        for (var i = 0; i < TourViewModel.Steps.Count; i++)
        {
            var dot = new BoxView { WidthRequest = i == _model.Index ? 18 : 6, HeightRequest = 6, CornerRadius = 3 };
            dot.SetDynamicResource(BoxView.ColorProperty, i == _model.Index ? "Accent" : "Line");
            _dots.Children.Add(dot);
        }

        _previous.IsEnabled = _model.CanGoBack;
        _previous.Opacity = _model.CanGoBack ? 1 : 0.4;
        _next.Text = _model.NextLabel;
    }
}
