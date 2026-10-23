using Coo.Mobile.Application.Growth;
using Coo.Mobile.Presentation.Growth;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The WHO growth chart: the 3rd–97th and 15th–85th percentile bands shaded, the median as a line, and the baby's
/// measurements as dots joined in order. Age in months along the bottom, kg or cm up the side.
/// </summary>
public sealed class GrowthChartView : GraphicsView
{
    private readonly ChartDrawable _drawable = new();

    public GrowthChartView()
    {
        Drawable = _drawable;
        HeightRequest = 280;
    }

    public void Show(IReadOnlyList<GrowthCurve> curves, IReadOnlyList<GrowthPoint> points, double maxAge, string unit, string monthsLabel, Palette palette)
    {
        (_drawable.Curves, _drawable.Points, _drawable.MaxAge, _drawable.Unit, _drawable.MonthsLabel, _drawable.Palette) =
            (curves, points, maxAge, unit, monthsLabel, palette);
        Invalidate();
    }

    private sealed class ChartDrawable : IDrawable
    {
        private const float Left = 34, Bottom = 36, Top = 22, Right = 8;

        public IReadOnlyList<GrowthCurve> Curves { get; set; } = [];

        public IReadOnlyList<GrowthPoint> Points { get; set; } = [];

        public double MaxAge { get; set; } = 3;

        public string Unit { get; set; } = "kg";

        public string MonthsLabel { get; set; } = "";

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (Curves.Count < 5)
                return;

            Color C(string hex, float alpha = 1) => Color.FromArgb(hex).WithAlpha(alpha);
            var plot = new RectF(rect.X + Left, rect.Y + Top, rect.Width - Left - Right, rect.Height - Top - Bottom);
            var values = Curves.SelectMany(c => c.Points.Select(p => p.Value)).Concat(Points.Select(p => p.Value)).Where(v => v > 0).ToList();
            var (min, max) = (values.Min(), values.Max());
            var pad = (max - min) * 0.05;
            (min, max) = (Math.Max(0, min - pad), max + pad);
            float X(double age) => plot.X + (float)(age / MaxAge * plot.Width);
            float Y(double value) => plot.Bottom - (float)((value - min) / (max - min) * plot.Height);

            // Bands between the outer and the inner lines, then the median.
            void Band(GrowthCurve low, GrowthCurve high, float alpha)
            {
                var path = new PathF();
                path.MoveTo(X(low.Points[0].Age), Y(low.Points[0].Value));
                foreach (var p in low.Points.Skip(1))
                    path.LineTo(X(p.Age), Y(p.Value));
                foreach (var p in high.Points.Reverse())
                    path.LineTo(X(p.Age), Y(p.Value));
                path.Close();
                canvas.FillColor = C(Palette.Accent, alpha);
                canvas.FillPath(path);
            }

            Band(Curves[0], Curves[4], 0.10f);
            Band(Curves[1], Curves[3], 0.14f);

            canvas.StrokeColor = C(Palette.Accent, 0.6f);
            canvas.StrokeSize = 1.5f;
            var median = new PathF();
            median.MoveTo(X(Curves[2].Points[0].Age), Y(Curves[2].Points[0].Value));
            foreach (var p in Curves[2].Points.Skip(1))
                median.LineTo(X(p.Age), Y(p.Value));
            canvas.DrawPath(median);

            // Axes: months along the bottom, a few values up the side.
            canvas.FontColor = C(Palette.Ink2);
            canvas.FontSize = 10;
            canvas.StrokeColor = C(Palette.Line);
            canvas.StrokeSize = 1;
            canvas.DrawLine(plot.X, plot.Bottom, plot.Right, plot.Bottom);
            var step = MaxAge <= 6 ? 1 : MaxAge <= 12 ? 2 : 3;
            for (var m = 0; m <= MaxAge + 0.01; m += step)
                canvas.DrawString(m.ToString(System.Globalization.CultureInfo.CurrentCulture), X(m) - 10, plot.Bottom + 4, 20, 14, HorizontalAlignment.Center, VerticalAlignment.Top);
            canvas.DrawString(MonthsLabel, plot.X, plot.Bottom + 18, plot.Width, 14, HorizontalAlignment.Center, VerticalAlignment.Top);
            for (var i = 0; i <= 4; i++)
            {
                var v = min + ((max - min) * i / 4);
                canvas.DrawString(v.ToString(v < 20 ? "0.#" : "0", System.Globalization.CultureInfo.CurrentCulture), rect.X, Y(v) - 7, Left - 4, 14, HorizontalAlignment.Right, VerticalAlignment.Center);
            }

            canvas.DrawString(Unit, rect.X, rect.Y, Left - 4, 14, HorizontalAlignment.Right, VerticalAlignment.Top);

            // The baby's own measurements, joined in time order.
            var shown = Points.Where(p => p.AgeMonths <= MaxAge).ToList();
            if (shown.Count > 1)
            {
                canvas.StrokeColor = C(Palette.AccentInk);
                canvas.StrokeSize = 2;
                for (var i = 1; i < shown.Count; i++)
                    canvas.DrawLine(X(shown[i - 1].AgeMonths), Y(shown[i - 1].Value), X(shown[i].AgeMonths), Y(shown[i].Value));
            }

            foreach (var p in shown)
            {
                canvas.FillColor = C(Palette.AccentInk);
                canvas.FillCircle(X(p.AgeMonths), Y(p.Value), 4.5f);
                canvas.FillColor = C(Palette.Surface);
                canvas.FillCircle(X(p.AgeMonths), Y(p.Value), 2f);
            }
        }
    }
}
