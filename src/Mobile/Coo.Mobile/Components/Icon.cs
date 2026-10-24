using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace Coo.Mobile.Components;

/// <summary>
/// A line icon (design system §5, Spec 005 §7): a vector path from <see cref="IconGeometry"/> on a 24 grid, stroked at
/// 1.7 with round caps and joins, never filled. Its colour is a palette resource key, so a palette swap recolours it.
/// </summary>
public sealed class Icon : ContentView
{
    private static readonly PathGeometryConverter Converter = new();

    public Icon(string geometry, string colourKey = "Ink", double size = 24)
    {
        var path = new Path
        {
            Data = (Geometry)Converter.ConvertFromInvariantString(geometry)!,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Aspect = Stretch.None,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Scale = size / 24,
            AnchorX = 0,
            AnchorY = 0,
        };
        path.SetDynamicResource(Shape.StrokeProperty, colourKey + "Brush");

        // MAUI draws a path from its own stroked bounds, not from 0,0 of its 24×24 grid: a glyph that does not touch the
        // grid's top-left (the mic runs from 5 to 19) moved up and left and looked off-centre in its circle (owner's
        // screenshot, 2026-10-24; measured 4.15 units = 5 minus half the stroke). Moved back by exactly that.
        var bounds = Microsoft.Maui.Graphics.PathBuilder.Build(geometry).Bounds;
        path.TranslationX = (bounds.X - (path.StrokeThickness / 2)) * size / 24;
        path.TranslationY = (bounds.Y - (path.StrokeThickness / 2)) * size / 24;
        Content = new Grid { WidthRequest = size, HeightRequest = size, Children = { path } };
        WidthRequest = size;
        HeightRequest = size;
        InputTransparent = true;
    }
}
