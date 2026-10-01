using System.ComponentModel;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Pages;

/// <summary>
/// The view for each <see cref="IHomeSection"/> (Spec 006 §3a). A new section type gets one more case here; an unknown
/// one renders as nothing rather than breaking Today. Each view re-renders when its section changes.
/// </summary>
internal static class SectionViews
{
    public static View Create(IHomeSection section, Action? onWordmarkLongPress = null) => section switch
    {
        BabyHeaderSection s => Live(s, () => Header(s, onWordmarkLongPress)),
        DueDateSection s => Live(s, () => DueDate(s)),
        StatusHeroSection s => Live(s, () => Hero(s)),
        RunningSection s => Live(s, () => RunningCards(s.Items, night: false)),
        LogTilesSection s => Live(s, () => Tiles(s)),
        TimelineSection s => Live(s, () => Timeline(s)),
        NightSection s => Live(s, () => Night(s, onWordmarkLongPress), nameof(NightSection.Title)),
        _ => new ContentView(),
    };

    /// <summary>
    /// Re-renders the content when the section raises PropertyChanged, at most once per UI frame. Properties listed in
    /// <paramref name="boundInPlace"/> are bound to their labels instead, so they never rebuild the view.
    /// </summary>
    private static ContentView Live(INotifyPropertyChanged section, Func<View> render, params string[] boundInPlace)
    {
        var view = new ContentView { Content = render() };
        var pending = false;
        section.PropertyChanged += (_, e) =>
        {
            if (pending || boundInPlace.Contains(e.PropertyName))
                return;
            pending = true;
            view.Dispatcher.Dispatch(() =>
            {
                pending = false;
                view.Content = render();
            });
        };
        return view;
    }

    private static View Header(BabyHeaderSection s, Action? longPress)
    {
        var mark = new Label
        {
            FormattedText = new FormattedString
            {
                Spans =
                {
                    new Span { Text = "co", FontFamily = "FigtreeBold", FontSize = 24 }.Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Ink")),
                    new Span { Text = "o", FontFamily = "FigtreeBold", FontSize = 24 }.Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Accent")),
                },
            },
            VerticalOptions = LayoutOptions.Center,
        };
        AttachLongPress(mark, longPress);

        var initial = Ui.Rounded(Ui.Text(s.Initial, "Meta", "CheekInk").Tap(l => { l.FontFamily = "FigtreeBold"; l.HorizontalOptions = LayoutOptions.Center; }), "Cheek", 999, new Thickness(0));
        initial.WidthRequest = 28;
        initial.HeightRequest = 28;
        var chip = Ui.Rounded(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { initial, Ui.Text($"{s.BabyName} · {s.Age}", "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center) },
        }, "Surface", 999, new Thickness(6, 6, 14, 6), "Line");

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(24, 16, 24, 8) };
        grid.Add(mark, 0);
        grid.Add(chip, 1);
        return grid;
    }

    private static View DueDate(DueDateSection s)
    {
        var arrived = new Button { Text = Strings.Get("Due.Arrived"), Command = s.ArrivedCommand, Margin = new Thickness(0, 12, 0, 0) };
        arrived.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        return new Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Text(Strings.Get("Expecting.Eyebrow").ToUpper(Strings.Culture), "Label", "SleepInk"),
                Ui.Text(s.Headline, "Title"),
                Ui.Text(s.DateText, "Body", "Ink2"),
                arrived,
            },
        }).Tap(c => c.Margin = new Thickness(24, 16, 24, 0));
    }

    private static View Hero(StatusHeroSection s) => new VerticalStackLayout
    {
        Padding = new Thickness(24, 20, 24, 4),
        Spacing = 6,
        Children =
        {
            Ui.Text(s.Eyebrow.ToUpper(Strings.Culture), "Label"),
            Ui.Text(s.Value, s.Eyebrow.Length == 0 ? "Section" : "Hero"),
            Ui.Text(s.Context, "Body", "Ink2"),
        },
    };

    internal static View RunningCards(IReadOnlyList<RunningItem> items, bool night)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, items.Count == 0 ? 0 : 12, 24, 0) };
        foreach (var item in items)
        {
            var look = EventAppearance.For(item.KindCode);
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, look.Mark + "Brush");
            var actions = new Grid { ColumnSpacing = 12 };
            for (var i = 0; i < item.Actions.Count; i++)
            {
                actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var a = item.Actions[i];
                var button = new Button { Text = a.Label, Command = a.Command };
                button.SetDynamicResource(VisualElement.StyleProperty, (night, a.IsPrimary) switch
                {
                    (true, true) => "NightPrimaryButton",
                    (true, false) => "NightGhostButton",
                    (false, true) => "PrimaryButton",
                    _ => "GhostButton",
                });
                actions.Add(button, i);
            }

            var body = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new HorizontalStackLayout { Spacing = 8, Children = { dot, Bound(item, nameof(RunningItem.Title), "Label", upper: true) } },
                    Bound(item, nameof(RunningItem.Digits), night ? "Timer" : "Window"),
                    Ui.Text(item.Started, "Meta"),
                    actions,
                },
            };
            stack.Add(night ? body : new Card(body));
        }

        return stack;
    }

    private static View Tiles(LogTilesSection s)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(24, 16, 24, 0) };
        for (var i = 0; i < s.Tiles.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var t = s.Tiles[i];
            grid.Add(new LogTile(t.KindCode, t.Name, t.LastAt, t.Command, fill: true), i);
        }

        return grid;
    }

    private static View Timeline(TimelineSection s)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(24, 24, 24, 24), Spacing = 4 };
        stack.Add(Ui.Text(Strings.Get("Section.Today"), "Section"));
        if (s.Items.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2").Tap(l => l.Margin = new Thickness(0, 8)));
        foreach (var row in s.Items)
            stack.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who));
        return stack;
    }

    private static View Night(NightSection s, Action? longPress)
    {
        var title = Bound(s, nameof(NightSection.Title), "Meta");
        AttachLongPress(title, longPress);
        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(24, 16, 24, 0) };
        top.Add(title.Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        top.Add(Ui.Rounded(Ui.Text(s.BabyName, "BodyStrong"), "Surface", 999, new Thickness(14, 8), "Line"), 1);

        var stack = new VerticalStackLayout { Spacing = 20 };
        stack.Add(top);
        if (s.Timer is { } timer)
            stack.Add(RunningCards([timer], night: true));

        var facts = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 8) };
        foreach (var fact in s.Facts)
        {
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, EventAppearance.For(fact.KindCode).Mark + "Brush");
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
            row.Add(dot, 0);
            row.Add(Ui.Text(fact.Label, "Body", "Ink2"), 1);
            row.Add(Ui.Text(fact.Value, "BodyStrong"), 2);
            facts.Add(row);
        }

        stack.Add(facts);

        var quick = new Grid { ColumnSpacing = 10, RowSpacing = 10, Padding = new Thickness(24, 0, 24, 24) };
        quick.ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)];
        for (var i = 0; i < s.Quick.Count; i++)
        {
            if (i % 3 == 0)
                quick.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var q = s.Quick[i];
            var look = EventAppearance.For(q.KindCode);
            var button = new Button { Text = q.Label, Command = q.Command, CornerRadius = 20, MinimumHeightRequest = 68, FontFamily = "FigtreeSemiBold", FontSize = 16 };
            button.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);
            button.SetDynamicResource(Button.TextColorProperty, look.Ink);
            quick.Add(button, i % 3, i / 3);
        }

        stack.Add(quick);
        return stack;
    }

    /// <summary>A label whose text follows one property of a model, updated in place.</summary>
    private static Label Bound(object source, string property, string style, bool upper = false)
    {
        var label = Ui.Text("", style);
        label.SetBinding(Label.TextProperty, new Binding(property, source: source, converter: upper ? UpperCase.Instance : null));
        return label;
    }

    private sealed class UpperCase : IValueConverter
    {
        public static readonly UpperCase Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            (value as string)?.ToUpper(Strings.Culture);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private static void AttachLongPress(View view, Action? action)
    {
#if DEBUG_SCREEN
        if (action is null)
            return;
        // A long press on the wordmark opens the debug screen in debug builds (Spec 006 Q5). MAUI has no long-press
        // gesture, so three quick taps stand in for it.
        view.GestureRecognizers.Add(new TapGestureRecognizer { NumberOfTapsRequired = 3, Command = new Command(action) });
#endif
    }
}
