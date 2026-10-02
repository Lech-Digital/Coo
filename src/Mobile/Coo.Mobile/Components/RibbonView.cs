using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The rhythm band (Spec 009b §2): sleep fills the band, feeds are pills of their real length above it, diapers dots
/// and pumps squares below it; quiet hours are a wash behind it, today has a "now" line. Drawn in the live palette.
/// A tap reports where in the day it fell.
/// </summary>
public sealed class RibbonView : GraphicsView
{
    private readonly BandDrawable _drawable = new();
    private PointF? _down;

    public RibbonView()
    {
        Drawable = _drawable;
        HeightRequest = 112;
        StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        EndInteraction += (_, e) =>
        {
            var up = e.Touches.FirstOrDefault();
            if (_down is { } down && e.IsInsideBounds && Width > 0 && Math.Abs(up.X - down.X) < 16)
                Tapped?.Invoke(this, Math.Clamp(up.X / Width, 0, 1));
            _down = null;
        };
    }

    /// <summary>The fraction of the day that was tapped.</summary>
    public event EventHandler<double>? Tapped;

    public void Show(IReadOnlyList<ChartItem> items, IReadOnlyList<ChartBand> quiet, IReadOnlyList<HourTick> hours, double? now, Palette palette)
    {
        _drawable.Items = items;
        _drawable.Quiet = quiet;
        _drawable.Hours = hours;
        _drawable.Now = now;
        _drawable.Palette = palette;
        Invalidate();
    }

    private sealed class BandDrawable : IDrawable
    {
        private const float Top = 34, BandHeight = 36;

        public IReadOnlyList<ChartItem> Items { get; set; } = [];

        public IReadOnlyList<ChartBand> Quiet { get; set; } = [];

        public IReadOnlyList<HourTick> Hours { get; set; } = [];

        public double? Now { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var left = rect.X + 1;
            var width = rect.Width - 2;
            float X(double fraction) => left + (float)(fraction * width);
            Color C(string hex) => Color.FromArgb(hex);

            foreach (var band in Quiet)
            {
                canvas.FillColor = C(Palette.SleepTint).WithAlpha(0.45f);
                canvas.FillRoundedRectangle(X(band.From), 14, X(band.To) - X(band.From), 84, 10);
            }

            var bar = new RectF(left, Top, width, BandHeight);
            canvas.FillColor = C(Palette.Surface);
            canvas.FillRoundedRectangle(bar, 12);
            canvas.StrokeColor = C(Palette.Line);
            canvas.StrokeSize = 1;
            canvas.DrawRoundedRectangle(bar, 12);
            foreach (var tick in Hours.Where(h => h.Hour % 3 == 0 && h.At is > 0 and < 1))
            {
                canvas.StrokeColor = C(Palette.Line);
                canvas.DrawLine(X(tick.At), Top + 2, X(tick.At), Top + BandHeight - 2);
            }

            foreach (var item in Items)
            {
                var x = X(item.From);
                switch (item.Shape)
                {
                    case ChartShape.Sleep:
                        canvas.FillColor = C(Palette.SleepMark).WithAlpha(0.85f);
                        canvas.FillRoundedRectangle(x + 0.5f, Top + 3, Math.Max(3, X(item.To) - x - 1), BandHeight - 6, 9);
                        break;
                    case ChartShape.Feed:
                        canvas.FillColor = C(Palette.FeedMark);
                        canvas.FillRoundedRectangle(x, Top - 16, Math.Max(7, X(item.To) - x), 11, 5.5f);
                        break;
                    case ChartShape.Diaper:
                        canvas.FillColor = C(Palette.DiaperMark);
                        canvas.FillCircle(x, Top + BandHeight + 10, 4.5f);
                        break;
                    case ChartShape.Pump:
                        canvas.FillColor = C(Palette.PumpMark);
                        canvas.FillRoundedRectangle(x - 4.5f, Top + BandHeight + 18, 9, 9, 2.5f);
                        break;
                }
            }

            if (Now is { } now)
            {
                canvas.StrokeColor = C(Palette.Accent);
                canvas.StrokeSize = 2;
                canvas.DrawLine(X(now), 14, X(now), 96);
                canvas.FillColor = C(Palette.Accent);
                canvas.FillCircle(X(now), 14, 3.5f);
            }

            foreach (var tick in Hours.Where(h => h.Hour % 3 == 0))
            {
                var major = tick.Hour % 6 == 0;
                canvas.FontColor = C(Palette.Ink2).WithAlpha(major ? 1f : 0.7f);
                canvas.FontSize = major ? 11 : 9;
                var x = X(tick.At);
                var align = tick.At <= 0 ? HorizontalAlignment.Left : tick.At >= 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                var boxX = tick.At <= 0 ? x : tick.At >= 1 ? x - 40 : x - 20;
                canvas.DrawString(tick.Hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture), boxX, 98, 40, 14, align, VerticalAlignment.Top);
            }
        }
    }
}
