namespace Coo.Mobile.Components;

/// <summary>
/// The "mochi" wordmark (<see cref="WordmarkGeometry"/>) drawn on one canvas, so the letters, the stem of the "i" and
/// the bunny dot share one coordinate system and one baseline. As separate shapes, each was placed and snapped on its
/// own and the stem stood a pixel or two off the letters (owner, 2026-11-06). The colours are bound to the palette's
/// resources and redraw when it changes.
/// </summary>
public sealed class WordmarkView : GraphicsView, IDrawable
{
    public static readonly BindableProperty InkColorProperty = BindableProperty.Create(nameof(InkColor), typeof(Color), typeof(WordmarkView), Colors.Black, propertyChanged: Redraw);

    public static readonly BindableProperty StemColorProperty = BindableProperty.Create(nameof(StemColor), typeof(Color), typeof(WordmarkView), Colors.Green, propertyChanged: Redraw);

    public static readonly BindableProperty DotColorProperty = BindableProperty.Create(nameof(DotColor), typeof(Color), typeof(WordmarkView), Colors.LightGreen, propertyChanged: Redraw);

    private static readonly Color InnerEar = Color.FromArgb("#F5C2C0");
    private static readonly Color Eyes = Color.FromArgb("#253A19");
    private static readonly PathF Letters = PathBuilder.Build(WordmarkGeometry.Letters);
    private static readonly PathF Stem = PathBuilder.Build(WordmarkGeometry.Stem);
    private static readonly PathF Dot = PathBuilder.Build(WordmarkGeometry.Dot);
    private static readonly PathF InnerEars = PathBuilder.Build(WordmarkGeometry.InnerEars);
    private static readonly PathF EyeLines = PathBuilder.Build(WordmarkGeometry.Eyes);

    /// <summary>The eyes and the pink of the ears, only where they can be seen: in the top bar they read as dashes and the
    /// dot is cleaner as a plain bunny (owner and design review, 2026-11-06).</summary>
    private readonly bool _detailed;

    /// <param name="fontSize">The size of the letters, as a font size; from 32 the bunny dot has its face.</param>
    public WordmarkView(double fontSize)
    {
        _detailed = fontSize >= 32;
        Drawable = this;
        WidthRequest = WordmarkGeometry.Width * fontSize / 100;
        HeightRequest = WordmarkGeometry.Height * fontSize / 100;
        BackgroundColor = Colors.Transparent;
        this.SetDynamicResource(InkColorProperty, "Ink");
        this.SetDynamicResource(StemColorProperty, "Accent");
        this.SetDynamicResource(DotColorProperty, "Brand");
    }

    public Color InkColor
    {
        get => (Color)GetValue(InkColorProperty);
        set => SetValue(InkColorProperty, value);
    }

    public Color StemColor
    {
        get => (Color)GetValue(StemColorProperty);
        set => SetValue(StemColorProperty, value);
    }

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var scale = (float)(Width / WordmarkGeometry.Width);
        canvas.SaveState();
        canvas.Antialias = true;
        canvas.Scale(scale, scale);
        canvas.FillColor = InkColor;
        canvas.FillPath(Letters);
        canvas.FillColor = StemColor;
        canvas.FillPath(Stem);
        canvas.FillColor = DotColor;
        canvas.FillPath(Dot);
        if (_detailed)
        {
            canvas.FillColor = InnerEar;
            canvas.FillPath(InnerEars);
            canvas.StrokeColor = Eyes;
            canvas.StrokeSize = (float)WordmarkGeometry.EyesStroke;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.DrawPath(EyeLines);
        }

        canvas.RestoreState();
    }

    private static void Redraw(BindableObject bindable, object oldValue, object newValue) => ((WordmarkView)bindable).Invalidate();
}
