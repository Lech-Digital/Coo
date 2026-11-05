using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Components;

/// <summary>The base components of design system §6 (Spec 005 §8). They take plain values and hold no logic.</summary>
internal static class Ui
{
    public static Label Text(string text, string style = "Body", string? colourKey = null)
    {
        var label = new Label { Text = text };
        label.SetDynamicResource(VisualElement.StyleProperty, style);
        if (colourKey is not null)
            label.SetDynamicResource(Label.TextColorProperty, colourKey);
        if (style is "Timer" or "Hero" or "Window")
            label.StyleClass = [Tabular.StyleClass];
        return label;
    }

    public static Border Rounded(View content, string backgroundKey, double radius, Thickness padding, string? borderKey = null)
    {
        var border = new Border
        {
            Content = content,
            Padding = padding,
            StrokeShape = new RoundRectangle { CornerRadius = radius },
            StrokeThickness = borderKey is null ? 0 : 1,
        };
        border.SetDynamicResource(VisualElement.BackgroundColorProperty, backgroundKey);
        if (borderKey is not null)
            border.SetDynamicResource(Border.StrokeProperty, borderKey + "Brush");
        return border;
    }

    /// <summary>A pushed page's top: the round back button and the title (as on Edit and Needs attention).</summary>
    public static Grid BackHeader(string title, Func<Task> back)
    {
        var button = Rounded(new Icon(IconGeometry.ChevronLeft, "Ink", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
        button.WidthRequest = button.HeightRequest = 48;
        button.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await back()) });
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        header.Add(button, 0);
        header.Add(Text(title, "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
        return header;
    }
}

/// <summary>Status only, never an action: <c>AccentSoft</c> fill, <c>AccentInk</c> text.</summary>
public sealed class Pill : ContentView
{
    public Pill(string text) =>
        Content = Ui.Rounded(Ui.Text(text, "Label", "AccentInk"), "AccentSoft", 999, new Thickness(10, 4));
}

/// <summary>Glass over the wash (<c>Glass</c>), radius 20, a hairline <c>GlassLine</c> border, and no shadow.</summary>
public sealed class Card : ContentView
{
    public Card(View content) => Content = Ui.Rounded(content, "Glass", 20, new Thickness(16), "GlassLine");
}

/// <summary>
/// A shortcut tile in the event tint: the kind's icon and a small "+" (it logs or starts that kind), the name, and the
/// last time with one word ("10:17 AM dirty"). One line each, cut with "…" rather than wrapped, so a long word in
/// another language never squeezes the tile (owner, 2026-11-05).
/// </summary>
public sealed class LogTile : ContentView
{
    public LogTile(string kindCode, string name, string lastAt, System.Windows.Input.ICommand? command = null, bool fill = false)
    {
        var look = EventAppearance.For(kindCode);
        var plus = new Border
        {
            WidthRequest = 20,
            HeightRequest = 20,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromRgba(255, 255, 255, 0.5),
            HorizontalOptions = LayoutOptions.End,
            Content = new Icon(IconGeometry.Plus, look.Ink, 14) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        };
        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        top.Add(new Icon(look.Icon, look.Ink, 20) { HorizontalOptions = LayoutOptions.Start }, 0);
        top.Add(plus, 1);

        // "9:52 AM · L": the time on one line and its word on the next; one string without a word ("asleep 10:38 AM")
        // may take two lines. Nothing is cut on a normal phone (owner, 2026-11-05).
        var split = lastAt.IndexOf(" · ", StringComparison.Ordinal);
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                top.Tap(t => t.Margin = new Thickness(0, 0, 0, 4)),
                Ui.Text(name, "Tile", look.Ink).Tap(l => { l.LineBreakMode = LineBreakMode.TailTruncation; l.FontSize = 14; }),
            },
        };
        if (split > 0)
        {
            stack.Add(Ui.Text(lastAt[..split], "Meta", look.Ink).Tap(l => { l.LineBreakMode = LineBreakMode.TailTruncation; l.FontSize = 13; }));
            stack.Add(Ui.Text(lastAt[(split + 3)..], "Meta", look.Ink).Tap(l => { l.LineBreakMode = LineBreakMode.TailTruncation; l.FontSize = 13; }));
        }
        else
        {
            stack.Add(Ui.Text(lastAt, "Meta", look.Ink).Tap(l => { l.MaxLines = 2; l.LineBreakMode = LineBreakMode.WordWrap; l.FontSize = 13; }));
        }

        var tile = Ui.Rounded(stack, look.Tint, 16, new Thickness(10));
        if (!fill)
            tile.WidthRequest = 96;
        tile.MinimumHeightRequest = 92;
        if (command is not null)
            tile.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
        SemanticProperties.SetDescription(tile, $"{name}, {lastAt}");
        Content = tile;
    }
}

/// <summary>Time (tabular), a 34 dp event badge, the title, and who logged it, over a hairline.</summary>
public sealed class TimelineRow : ContentView
{
    /// <param name="inGroup">In a <see cref="PageFrame.Group"/>: the group draws the hairlines and the side padding is the row's.</param>
    public TimelineRow(string time, string kindCode, string title, string who, Action? onTap = null, bool inGroup = false)
    {
        var look = EventAppearance.For(kindCode);
        var badge = Ui.Rounded(new Icon(look.Icon, look.Ink, 20), look.Tint, 12, new Thickness(7));
        badge.WidthRequest = 34;
        badge.HeightRequest = 34;

        var clock = Ui.Text(time, "Meta");
        clock.StyleClass = [Tabular.StyleClass];
        // "14:05" fits 48; "1:21 PM" (12-hour phones) broke over two lines in it (seen on the demo build).
        // Scaled with the text size: at 1.3× "10:38 AM" broke over two lines again (2026-11-05).
        clock.WidthRequest = (time.Length <= 5 ? 48 : 68) * TextScale.Factor;
        clock.VerticalOptions = LayoutOptions.Center;

        var titleLabel = Ui.Text(title, "BodyStrong");
        titleLabel.VerticalOptions = LayoutOptions.Center;
        var whoLabel = Ui.Text(who, "Meta");
        whoLabel.VerticalOptions = LayoutOptions.Center;

        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 12,
            Padding = inGroup ? new Thickness(16, 9) : new Thickness(0, 10),
        };
        grid.Add(clock, 0);
        grid.Add(badge, 1);
        grid.Add(titleLabel, 2);
        grid.Add(whoLabel, 3);

        if (inGroup)
        {
            Content = grid;
        }
        else
        {
            var line = new BoxView { HeightRequest = 1 };
            line.SetDynamicResource(BoxView.ColorProperty, "Line");
            Content = new VerticalStackLayout { Children = { grid, line } };
        }

        if (onTap is not null)
            GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
    }
}

/// <summary>A midnight divider row separating consecutive days in the timeline.</summary>
public sealed class MidnightRow : ContentView
{
    public MidnightRow(string label)
    {
        var lineLeft = new BoxView { HeightRequest = 1, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center };
        lineLeft.SetDynamicResource(BoxView.ColorProperty, "Line");

        var text = Ui.Text(label, "Meta", "Ink2");
        text.VerticalOptions = LayoutOptions.Center;
        text.HorizontalTextAlignment = TextAlignment.Center;

        var lineRight = new BoxView { HeightRequest = 1, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center };
        lineRight.SetDynamicResource(BoxView.ColorProperty, "Line");

        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 10,
            Padding = new Thickness(0, 16, 0, 10),
        };
        grid.Add(lineLeft, 0);
        grid.Add(text, 1);
        grid.Add(lineRight, 2);
        Content = grid;
    }
}

/// <summary>
/// One of the Log's totals (owner, 2026-11-05: "more contentful"): the kind's icon and word, the number, what it is made
/// of, and how it compares with the usual day. The two small lines are left out when empty, and never wrap.
/// </summary>
public sealed class TotalsCard : ContentView
{
    public TotalsCard(string kindCode, string number, string unit, string detail, string compare)
    {
        var look = EventAppearance.For(kindCode);
        var head = new HorizontalStackLayout { Spacing = 6 };
        head.Add(new Icon(look.Icon, look.Ink, 16) { VerticalOptions = LayoutOptions.Center });
        var word = string.IsNullOrEmpty(unit) ? unit : char.ToUpper(unit[0], Strings.Culture) + unit[1..];
        head.Add(Ui.Text(word, "Meta", look.Ink).Tap(l =>
        {
            l.FontFamily = "FigtreeSemiBold";
            l.MaxLines = 1;
            l.LineBreakMode = LineBreakMode.TailTruncation;
            l.VerticalOptions = LayoutOptions.Center;
        }));

        var value = Ui.Text(number, "Section", look.Ink);
        value.StyleClass = [Tabular.StyleClass];
        value.FontSize = 24;
        value.MaxLines = 1;
        value.LineBreakMode = LineBreakMode.NoWrap;

        var stack = new VerticalStackLayout { Spacing = 2, Children = { head, value } };
        // Up to two lines each: "Last 9:52 AM · usually 6" did not fit half the width at 1.3× (2026-11-05).
        foreach (var (text, opacity) in new[] { (detail, 1.0), (compare, 0.8) })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            stack.Add(Ui.Text(text, "Meta", look.Ink).Tap(l =>
            {
                l.FontSize = 12;
                l.Opacity = opacity;
                l.MaxLines = 2;
                l.LineBreakMode = LineBreakMode.TailTruncation;
            }));
        }

        var rounded = Ui.Rounded(stack, look.Tint, 18, new Thickness(12));
        rounded.MinimumHeightRequest = 104;
        Content = rounded;
    }
}

/// <summary>
/// The night timer: an eyebrow with an event dot, <c>Timer</c> digits ticking each second, the start time, and two
/// 64 dp actions. <c>Stop</c> is the only accent.
/// </summary>
public sealed class NightTimer : ContentView
{
    private readonly Label _digits;
    private readonly DateTimeOffset _start;
    private readonly TimeProvider _time;
    private readonly IDispatcherTimer? _tick;

    public NightTimer(string kindCode, string eyebrow, DateTimeOffset start, TimeProvider time, string secondary)
    {
        _start = start;
        _time = time;
        var look = EventAppearance.For(kindCode);

        var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
        dot.SetDynamicResource(Shape.FillProperty, look.Mark + "Brush");
        var head = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { dot, Ui.Text(eyebrow.ToUpper(System.Globalization.CultureInfo.CurrentCulture), "Label") },
        };

        _digits = Ui.Text(Format(), "Timer");
        var stop = new Button { Text = "Stop" };
        stop.SetDynamicResource(VisualElement.StyleProperty, "NightPrimaryButton");
        var other = new Button { Text = secondary };
        other.SetDynamicResource(VisualElement.StyleProperty, "NightGhostButton");

        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 12 };
        actions.Add(stop, 0);
        actions.Add(other, 1);

        Content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { head, _digits, Ui.Text($"since {start:HH:mm}", "Meta"), actions },
        };

        if (Dispatcher is { } dispatcher)
        {
            _tick = dispatcher.CreateTimer();
            _tick.Interval = TimeSpan.FromSeconds(1);
            _tick.Tick += (_, _) => _digits.Text = Format();
            _tick.Start();
        }
    }

    private string Format()
    {
        var elapsed = _time.GetUtcNow() - _start;
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Configures a view inline while building a tree in C#.</summary>
internal static class ViewExtensions
{
    public static T Tap<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }
}

/// <summary>
/// The phone's text size (Android Settings → Display → Font size), at least 1. Layouts that size things in dp use it
/// so large text does not break words or cut labels (OnePlus 8 Pro and a 360 dp phone at 1.3×, 2026-11-05).
/// </summary>
public static class TextScale
{
    public static double Factor => Math.Max(1, (double)(global::Android.App.Application.Context.Resources?.Configuration?.FontScale ?? 1f));
}

/// <summary>
/// Equal cells in one row while each gets at least <c>minWidth</c> dp (scaled with the text size), otherwise in two
/// columns: the four care tiles became "Diape" and "oz pu mped" on a 360 dp phone with large text.
/// </summary>
public static class AdaptiveColumns
{
    public static Grid Create(IReadOnlyList<View> cells, double minWidth, Thickness padding, double spacing = 8)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var grid = new Grid { ColumnSpacing = spacing, RowSpacing = spacing, Padding = padding };
        var columns = 0;

        void Arrange(int count)
        {
            columns = count;
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var c = 0; c < count; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var r = 0; r < (cells.Count + count - 1) / count; r++)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < cells.Count; i++)
                grid.Add(cells[i], i % count, i / count);
        }

        Arrange(Math.Max(1, cells.Count));
        grid.SizeChanged += (_, _) =>
        {
            var n = cells.Count;
            if (grid.Width <= 0 || n <= 2)
                return;
            var inner = grid.Width - padding.Left - padding.Right;
            var each = (inner - (spacing * (n - 1))) / n;
            var wanted = each >= minWidth * TextScale.Factor ? n : (n + 1) / 2;
            if (wanted != columns)
                grid.Dispatcher.Dispatch(() => Arrange(wanted));
        };
        return grid;
    }
}
