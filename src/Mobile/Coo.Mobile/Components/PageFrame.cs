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

    public const double WordmarkSize = 24;

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
}
