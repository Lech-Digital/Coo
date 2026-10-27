using Microsoft.Maui.Graphics;

namespace Coo.Mobile.Components;

/// <summary>
/// A line icon (design system §5, Spec 005 §7): a vector path from <see cref="Presentation.Theme.IconGeometry"/> on a 24
/// grid, stroked at 1.7 with round caps and joins, never filled. Its colour is a palette resource key, so a palette swap
/// recolours it.
/// </summary>
/// <remarks>
/// Drawn on its own canvas, the 24 grid scaled to the box and centred in it. A Shapes <c>Path</c> placed itself by its
/// stroked bounds instead of by the grid, differently per glyph: the mic sat up and left, and the correction for that
/// pushed the back chevron down and right (owner's screenshots, 2026-10-24 and 2026-10-27). Here the grid sets only the
/// scale, and each glyph is centred on its own shape.
/// </remarks>
public sealed class Icon : GraphicsView
{
    public static readonly BindableProperty StrokeColourProperty = BindableProperty.Create(
        nameof(StrokeColour), typeof(Color), typeof(Icon), Colors.Black, propertyChanged: (view, _, _) => ((Icon)view).Invalidate());

    public Icon(string geometry, string colourKey = "Ink", double size = 24)
    {
        Drawable = new Glyph(this, PathBuilder.Build(geometry));
        BackgroundColor = Colors.Transparent;
        WidthRequest = size;
        HeightRequest = size;
        InputTransparent = true;
        SetDynamicResource(StrokeColourProperty, colourKey);
    }

    public Color StrokeColour
    {
        get => (Color)GetValue(StrokeColourProperty);
        set => SetValue(StrokeColourProperty, value);
    }

    private sealed class Glyph(Icon icon, PathF path) : IDrawable
    {
        private const float Grid = 24f;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var bounds = new RectF(0, 0, (float)icon.Width, (float)icon.Height);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                bounds = dirtyRect;
            // The glyph's own centre on the box's centre, at the grid's scale: a glyph drawn off the grid's middle (the half
            // disc of a breast side) sits centred too.
            var scale = Math.Min(bounds.Width, bounds.Height) / Grid;
            var glyph = path.Bounds;
            canvas.SaveState();
            canvas.Translate(bounds.Center.X - (glyph.Center.X * scale), bounds.Center.Y - (glyph.Center.Y * scale));
            canvas.Scale(scale, scale);
            canvas.StrokeColor = icon.StrokeColour;
            canvas.StrokeSize = 1.7f;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);
            canvas.RestoreState();
        }
    }
}
