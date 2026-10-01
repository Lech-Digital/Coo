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
        Content = new Grid { WidthRequest = size, HeightRequest = size, Children = { path } };
        WidthRequest = size;
        HeightRequest = size;
        InputTransparent = true;
    }
}
