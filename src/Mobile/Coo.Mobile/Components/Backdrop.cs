using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The soft colour wash behind every page (owner, 2026-11-05: "colourful, dimmed and soothing"): matcha at the top
/// right, lavender on the left, peach at the bottom, over a green-to-cream ground; by night the same in forest, dusk
/// violet and ember over near-black. Cards are glass over it (<c>Glass</c>, <c>GlassLine</c>).
/// </summary>
public sealed class Backdrop : Grid
{
    public Backdrop()
    {
        InputTransparent = true;
        // One picture of the wash per palette (WashImage), drawn once: four gradient layers redrawn on every frame made
        // tab switches lag (2026-11-05).
        var wash = new Image { Aspect = Aspect.Fill };
        wash.SetDynamicResource(Image.SourceProperty, "WashImage");
        Children.Add(wash);
    }

    /// <summary>A page's content over the wash.</summary>
    public static Grid Behind(View content)
    {
        var grid = new Grid();
        grid.Add(new Backdrop());
        grid.Add(content);
        return grid;
    }
}

/// <summary>The resources the wash, the glass cards and the sheet's veil take, per palette; added beside the tokens.</summary>
public static class WashResources
{
    public static IEnumerable<(string Key, object Value)> For(PaletteKind kind)
    {
        var night = kind == PaletteKind.Night;
        static Brush Radial(Point centre, double radius, string hex) => new RadialGradientBrush
        {
            Center = centre,
            Radius = radius,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(hex), 0f),
                new GradientStop(Color.FromArgb(hex).WithAlpha(0), 1f),
            },
        };

        yield return ("WashBase", new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(night ? "#121511" : "#E2EAD8"), 0f),
                new GradientStop(Color.FromArgb(night ? "#14120F" : "#F2EBE0"), 1f),
            },
        });
#if ANDROID
        yield return ("WashImage", ImageSource.FromFile(Platforms.Android.WashBitmap.For(kind)));
#endif
        yield return ("WashTop", Radial(new Point(1, 0), 0.75, night ? "#1F2C1C" : "#BFD6AE"));
        yield return ("WashSide", Radial(new Point(0, 0.42), 0.6, night ? "#221D33" : "#D3C9EC"));
        yield return ("WashBottom", Radial(new Point(0.2, 1), 0.7, night ? "#2B1D16" : "#F2CDB4"));

        // Glass: the cards let the wash through a little.
        // Hex with alpha: Color.FromRgba(27, 31, 25, 0.82) takes the 0–1 overload and clamps every channel to white.
        var glass = Color.FromArgb(night ? "#D11B1F19" : "#D1FAFAF5");
        var glassLine = Color.FromArgb(night ? "#2C3228" : "#CCD8DACB");
        yield return ("Glass", glass);
        yield return ("GlassBrush", new SolidColorBrush(glass));
        yield return ("GlassLine", glassLine);
        yield return ("GlassLineBrush", new SolidColorBrush(glassLine));

        // The veil behind a sheet: lighter at night, so the wash still shows around the drawer.
        yield return ("Scrim", night ? Colors.Black : Color.FromArgb("#262A22"));
        yield return ("ScrimOpacity", night ? 0.18 : 0.4);

        // The Premium tag beside the wordmark.
        yield return ("PremiumTag", new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(night ? "#3E3213" : "#F7E7B6"), 0f),
                new GradientStop(Color.FromArgb(night ? "#5A4719" : "#E8C46E"), 1f),
            },
        });
        yield return ("PremiumTagInk", Color.FromArgb(night ? "#F3D27A" : "#6E4F0E"));
    }
}
