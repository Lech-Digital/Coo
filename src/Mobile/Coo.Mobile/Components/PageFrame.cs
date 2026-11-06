using System.Windows.Input;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The one frame every tab shares (owner, 2026-11-05: "make a template and reuse it"): the top bar's height and padding,
/// the wordmark size, and the day/night switch at the far right. Today, Night and the other tabs all build their bar
/// from <see cref="Bar"/>, so they line up to the pixel.
/// </summary>
public static class PageFrame
{
    /// <summary>The side margin of every page's content, in dp.</summary>
    public const double Side = 24;

    public const double HeaderHeight = 60;

    public const double WordmarkSize = 28;

    /// <summary>The 44 dp switch sits 10 dp inside the right margin, so its icon lines up with the content edge.</summary>
    public static Thickness HeaderPadding => new(Side, 12, Side - 10, 4);

    /// <summary>The bar: the wordmark on the left, then the page's actions, then the day/night switch at the far right.</summary>
    public static Grid Bar(View brand, View themeSwitch, params View[] actions)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 4,
            Padding = HeaderPadding,
            MinimumHeightRequest = HeaderHeight,
        };
        brand.VerticalOptions = LayoutOptions.Center;
        grid.Add(brand, 0);
        if (actions.Length > 0)
        {
            var row = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
            foreach (var action in actions)
                row.Add(action);
            grid.Add(row, 2);
        }

        grid.Add(themeSwitch, 3);
        return grid;
    }

    public static View ThemeSwitch(ThemeController theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return ThemeSwitch(theme.Current == PaletteKind.Night, new Command(async () => await theme.ToggleAsync(CancellationToken.None)));
    }

    /// <summary>A sun by night, a moon by day: 44 dp to tap, the icon in <c>Ink2</c>.</summary>
    public static View ThemeSwitch(bool isNight, ICommand toggle)
    {
        var view = new ContentView
        {
            Content = new Icon(isNight ? IconGeometry.Sun : IconGeometry.Sleep, "Ink2") { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            WidthRequest = 44,
            HeightRequest = 44,
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(view, Strings.Get(isNight ? "Theme.ToDay" : "Theme.ToNight"));
        view.GestureRecognizers.Add(new TapGestureRecognizer { Command = toggle });
        return view;
    }

    /// <summary>The page's title, 28 sp semibold, with an optional line under it ("Lena · 7 weeks · Thursday 5 November").</summary>
    public static View Title(string title, string? subtitle = null, View? leading = null)
    {
        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        text.Add(new Label { Text = title, FontFamily = "FigtreeSemiBold", FontSize = 28, CharacterSpacing = -0.4 }
            .Tap(l => l.SetDynamicResource(Label.TextColorProperty, "Ink")));
        if (!string.IsNullOrEmpty(subtitle))
            text.Add(Ui.Text(subtitle, "Meta", "Ink2").Tap(l => l.FontSize = 14));
        if (leading is null)
            return text;
        var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        row.Add(leading, 0);
        row.Add(text, 1);
        return row;
    }

    /// <summary>A section's label: 12 sp, letter-spaced capitals in <c>Ink2</c> ("AN AVERAGE DAY").</summary>
    public static Label SectionLabel(string text) =>
        new Label { Text = text.ToUpper(Strings.Culture), FontFamily = "FigtreeSemiBold", FontSize = 12, CharacterSpacing = 1.2 }
            .Tap(l => l.SetDynamicResource(Label.TextColorProperty, "Ink2"));

    /// <summary>A grouped list: one glass card, its rows divided by hairlines (Settings, Stats, the Log's entries).</summary>
    public static View Group(IEnumerable<View> rows)
    {
        var stack = new VerticalStackLayout();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
            {
                var line = new BoxView { HeightRequest = 1, Margin = new Thickness(16, 0) };
                line.SetDynamicResource(BoxView.ColorProperty, "GlassLine");
                stack.Add(line);
            }

            stack.Add(row);
            first = false;
        }

        return Ui.Rounded(stack, "Glass", 20, new Thickness(0), "GlassLine");
    }

    /// <summary>One row of a group: a title, an optional line under it, an optional value, and a chevron when it opens something.</summary>
    public static View Row(string title, string? subtitle = null, string? value = null, Func<Task>? open = null, View? leading = null, View? trailing = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 12,
            Padding = new Thickness(16, 10),
            MinimumHeightRequest = 52,
        };
        if (leading is not null)
            grid.Add(leading.Tap(v => v.VerticalOptions = LayoutOptions.Center), 0);
        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 1 };
        text.Add(Ui.Text(title, "Body").Tap(l => l.FontFamily = "FigtreeMedium"));
        if (!string.IsNullOrEmpty(subtitle))
            text.Add(Ui.Text(subtitle, "Meta", "Ink2"));
        grid.Add(text, 1);
        if (!string.IsNullOrEmpty(value))
            grid.Add(Ui.Text(value, "Meta", "Ink2").Tap(l => { l.VerticalOptions = LayoutOptions.Center; l.FontSize = 14; }), 2);
        if (trailing is not null)
            grid.Add(trailing.Tap(v => v.VerticalOptions = LayoutOptions.Center), 3);
        else if (open is not null)
            grid.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 18) { VerticalOptions = LayoutOptions.Center }, 3);
        if (open is not null)
        {
            grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await open()) });
            SemanticProperties.SetDescription(grid, title);
        }

        return grid;
    }

    /// <summary>The main action: 52 dp, full width, matcha (the accent).</summary>
    public static Button Primary(string text, Func<Task> action) => Button(text, action, primary: true, height: 52);

    /// <summary>A second action beside or under it: 48 dp, glass with a hairline.</summary>
    public static Button Secondary(string text, Func<Task> action) => Button(text, action, primary: false, height: 48);

    private static Button Button(string text, Func<Task> action, bool primary, double height)
    {
        var button = new Button
        {
            Text = text,
            HeightRequest = height,
            CornerRadius = (int)(height / 2),
            FontFamily = "FigtreeSemiBold",
            FontSize = primary ? 17 : 15,
            Padding = new Thickness(16, 0),
        };
        button.SetDynamicResource(VisualElement.BackgroundColorProperty, primary ? "Accent" : "Glass");
        button.SetDynamicResource(Microsoft.Maui.Controls.Button.TextColorProperty, primary ? "OnAccent" : "Ink");
        if (!primary)
        {
            button.BorderWidth = 1;
            button.SetDynamicResource(Microsoft.Maui.Controls.Button.BorderColorProperty, "GlassLine");
        }

        button.Clicked += async (_, _) => await action();
        return button;
    }

    /// <summary>
    /// A segmented choice ("7 days | 14 days | 30 days", "Now | Earlier…"): the chosen one on the soft matcha pill, like
    /// the chosen tab; solid accent is kept for actions (design review, 2026-11-06).
    /// </summary>
    public static View Segmented(IReadOnlyList<string> options, int selected, Action<int> choose)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(choose);
        var grid = new Grid { ColumnSpacing = 0, Padding = new Thickness(4) };
        for (var i = 0; i < options.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var index = i;
            var on = i == selected;
            var label = new Label
            {
                Text = options[i],
                FontFamily = "FigtreeSemiBold",
                FontSize = 14,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            label.SetDynamicResource(Label.TextColorProperty, on ? "AccentInk" : "Ink");
            var cell = new Border
            {
                Content = label,
                HeightRequest = 36,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            };
            if (on)
                cell.SetDynamicResource(VisualElement.BackgroundColorProperty, "AccentSoft");
            else
                cell.BackgroundColor = Colors.Transparent;
            cell.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => choose(index)) });
            SemanticProperties.SetDescription(cell, options[i]);
            grid.Add(cell, i);
        }

        return Ui.Rounded(grid, "Glass", 22, new Thickness(0), "GlassLine");
    }
}
