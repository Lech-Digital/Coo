using Android.Graphics;
using Android.Graphics.Drawables;
using Coo.Mobile.Presentation.Theme;
using AColor = Android.Graphics.Color;

namespace Coo.Mobile.Platforms.Android;

/// <summary>
/// The colour wash as one small picture per palette, drawn once (owner, 2026-11-05: switching tabs lagged). Four
/// full-screen gradient layers were painted again on every frame of every page; the picture is one layer, a quarter of
/// the screen's size and stretched, which a gradient survives without a visible step.
/// </summary>
internal static class WashBitmap
{
    private const int Shrink = 4;
    private static readonly Dictionary<PaletteKind, string> Files = [];

    /// <summary>The picture's file for a palette, drawn on first use for this screen's size.</summary>
    public static string For(PaletteKind kind)
    {
        lock (Files)
        {
            if (Files.TryGetValue(kind, out var known) && File.Exists(known))
                return known;
            var metrics = global::Android.App.Application.Context.Resources?.DisplayMetrics;
            var (width, height) = (Math.Max(1, (metrics?.WidthPixels ?? 1080) / Shrink), Math.Max(1, (metrics?.HeightPixels ?? 2400) / Shrink));
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, $"wash-v1-{kind}-{width}x{height}.png");
            if (!File.Exists(path))
            {
                using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
                using var canvas = new Canvas(bitmap);
                using var wash = Drawable(kind == PaletteKind.Night, Math.Max(width, height));
                wash.SetBounds(0, 0, width, height);
                wash.Draw(canvas);
                using var stream = File.Create(path);
                bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            }

            Files[kind] = path;
            return path;
        }
    }

    /// <summary>The same wash as <see cref="Components.WashResources"/>: a ground and three soft glows.</summary>
    public static LayerDrawable Drawable(bool night, int size)
    {
        var ground = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, [
            AColor.ParseColor(night ? "#121511" : "#E2EAD8"), AColor.ParseColor(night ? "#14120F" : "#F2EBE0")]);
        GradientDrawable Glow(string hex, float x, float y, float radius)
        {
            var colour = AColor.ParseColor(hex);
            var glow = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, [colour, AColor.Argb(0, colour.R, colour.G, colour.B)]);
            glow.SetGradientType(GradientType.RadialGradient);
            glow.SetGradientCenter(x, y);
            glow.SetGradientRadius(radius * size);
            return glow;
        }

        return new LayerDrawable([
            ground,
            Glow(night ? "#1F2C1C" : "#BFD6AE", 1f, 0f, 0.75f),
            Glow(night ? "#221D33" : "#D3C9EC", 0f, 0.42f, 0.6f),
            Glow(night ? "#2B1D16" : "#F2CDB4", 0.2f, 1f, 0.7f),
        ]);
    }
}
