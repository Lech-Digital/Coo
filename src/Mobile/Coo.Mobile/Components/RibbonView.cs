using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The day ribbon (design system §6, Spec 006 §7): sleep as tinted blocks outlined in the sleep mark, other events as
/// 4 dp ticks on staggered lanes, with hour labels. Drawn from fractions of the day, in the live palette.
/// </summary>
public sealed class RibbonView : GraphicsView
{
    private readonly RibbonDrawable _drawable = new();

    public RibbonView()
    {
        Drawable = _drawable;
        HeightRequest = 64;
    }

    public void Show(IReadOnlyList<RibbonMark> marks, Palette palette)
    {
        _drawable.Marks = marks;
        _drawable.Palette = palette;
        Invalidate();
    }

    private sealed class RibbonDrawable : IDrawable
    {
        public IReadOnlyList<RibbonMark> Marks { get; set; } = [];

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            const float barHeight = 44;
            var bar = new RectF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, barHeight);
            canvas.FillColor = Color.FromArgb(Palette.Surface);
            canvas.FillRoundedRectangle(bar, 14);
            canvas.StrokeColor = Color.FromArgb(Palette.Line);
            canvas.StrokeSize = 1;
            canvas.DrawRoundedRectangle(bar, 14);

            foreach (var mark in Marks.Where(m => m.IsBlock))
            {
                var x = bar.X + (float)(mark.From * bar.Width);
                var width = Math.Max(3f, (float)((mark.To - mark.From) * bar.Width));
                var block = new RectF(x, bar.Y + 6, width, barHeight - 12);
                canvas.FillColor = Color.FromArgb(Palette.SleepTint);
                canvas.FillRoundedRectangle(block, 8);
                canvas.StrokeColor = Color.FromArgb(Palette.SleepMark);
                canvas.DrawRoundedRectangle(block, 8);
            }

            foreach (var mark in Marks.Where(m => !m.IsBlock))
            {
                var (colour, top, height) = mark.KindCode switch
                {
                    "diaper" => (Palette.DiaperMark, 26f, 12f),
                    "pump" => (Palette.PumpMark, 16f, 14f),
                    _ => (Palette.FeedMark, 6f, barHeight - 12),
                };
                canvas.FillColor = Color.FromArgb(colour);
                canvas.FillRoundedRectangle(bar.X + (float)(mark.From * bar.Width) - 2, bar.Y + top, 4, height, 2);
            }

            canvas.FontColor = Color.FromArgb(Palette.Ink2);
            canvas.FontSize = 11;
            for (var h = 0; h <= 24; h += 6)
            {
                var x = bar.X + (bar.Width * h / 24f);
                var align = h == 0 ? HorizontalAlignment.Left : h == 24 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                var boxX = h == 0 ? x : h == 24 ? x - 40 : x - 20;
                canvas.DrawString(h.ToString("00", System.Globalization.CultureInfo.InvariantCulture), boxX, barHeight + 4, 40, 16, align, VerticalAlignment.Top);
            }
        }
    }
}
