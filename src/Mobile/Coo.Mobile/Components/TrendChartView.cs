using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The statistics bar chart: one bar per day, week or month in the kind's colour, the selected one solid, one still
/// going washed out with an outline, a dashed line at the average. A tap reports which bar it fell on.
/// </summary>
public sealed class TrendChartView : GraphicsView
{
    private readonly BarsDrawable _drawable = new();
    private PointF? _down;

    public TrendChartView()
    {
        Drawable = _drawable;
        HeightRequest = 196;
        StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        EndInteraction += (_, e) =>
        {
            var up = e.Touches.FirstOrDefault();
            if (_down is { } down && e.IsInsideBounds && Width > 0 && _drawable.Bars.Count > 0 && Math.Abs(up.X - down.X) < 16)
                Tapped?.Invoke(this, Math.Clamp((int)(up.X / (Width / _drawable.Bars.Count)), 0, _drawable.Bars.Count - 1));
            _down = null;
        };
    }

    /// <summary>The index of the bar that was tapped.</summary>
    public event EventHandler<int>? Tapped;

    public void Show(IReadOnlyList<TrendBar> bars, double? average, string kindCode, Palette palette)
    {
        _drawable.Bars = bars;
        _drawable.Average = average;
        _drawable.Mark = kindCode switch
        {
            "feeding" => palette.FeedMark,
            "diaper" => palette.DiaperMark,
            "pump" => palette.PumpMark,
            _ => palette.SleepMark,
        };
        _drawable.Palette = palette;
        Invalidate();
    }

    private sealed class BarsDrawable : IDrawable
    {
        private const float Top = 8, LabelHeight = 32;

        public IReadOnlyList<TrendBar> Bars { get; set; } = [];

        public double? Average { get; set; }

        public string Mark { get; set; } = WashiPalette.Day.SleepMark;

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (Bars.Count == 0)
                return;

            Color C(string hex) => Color.FromArgb(hex);
            var baseline = rect.Bottom - LabelHeight;
            var height = baseline - Top;
            var max = Math.Max(Bars.Max(b => b.Value), Average ?? 0);
            max = max <= 0 ? 1 : max * 1.1;
            float Y(double value) => baseline - (float)(value / max * height);

            var slot = rect.Width / Bars.Count;
            var barWidth = Math.Min(slot * 0.62f, 36);

            canvas.StrokeColor = C(Palette.Line);
            canvas.StrokeSize = 1;
            canvas.DrawLine(rect.X, baseline, rect.Right, baseline);

            for (var i = 0; i < Bars.Count; i++)
            {
                var bar = Bars[i];
                var center = rect.X + slot * (i + 0.5f);
                var x = center - barWidth / 2;

                if (!bar.HasData)
                {
                    canvas.FillColor = C(Palette.Line);
                    canvas.FillCircle(center, baseline - 4, 2.5f);
                }
                else
                {
                    var top = Math.Min(Y(bar.Value), baseline - 3);
                    var box = new RectF(x, top, barWidth, baseline - top);
                    canvas.FillColor = C(Mark).WithAlpha(bar.IsSelected ? 1f : bar.IsPartial ? 0.22f : 0.45f);
                    canvas.FillRoundedRectangle(box, Math.Min(6, barWidth / 3));
                    if (bar.IsPartial)
                    {
                        canvas.StrokeColor = C(Mark);
                        canvas.StrokeSize = 1.5f;
                        canvas.StrokeDashPattern = [3, 3];
                        canvas.DrawRoundedRectangle(box, Math.Min(6, barWidth / 3));
                        canvas.StrokeDashPattern = null;
                    }
                }

                canvas.FontColor = C(bar.IsSelected ? Palette.Ink : Palette.Ink2);
                canvas.FontSize = bar.IsSelected ? 11 : 10;
                canvas.DrawString(bar.Label, center - slot / 2, baseline + 4, slot, LabelHeight - 4, HorizontalAlignment.Center, VerticalAlignment.Top);
            }

            if (Average is { } average)
            {
                canvas.StrokeColor = C(Palette.Ink2).WithAlpha(0.8f);
                canvas.StrokeSize = 1;
                canvas.StrokeDashPattern = [5, 4];
                canvas.DrawLine(rect.X, Y(average), rect.Right, Y(average));
                canvas.StrokeDashPattern = null;
            }
        }
    }
}
