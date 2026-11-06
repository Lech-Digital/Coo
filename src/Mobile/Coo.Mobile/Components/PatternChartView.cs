using Coo.Mobile.Application.Insights;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The sleep pattern (owner, 2026-11-06): one column per day, side by side, oldest on the left, each running from noon at
/// the top to the next noon at the bottom, so a night stays in one piece. Sleeps are bars in the sleep colour, the day's
/// longest one solid; feeds are short ticks across the column, diapers dots beside it; a dashed line marks when the
/// longest sleep usually starts. A tap reports the column, to open that day in the Log.
/// </summary>
public sealed class PatternChartView : GraphicsView
{
    private readonly PatternDrawable _drawable = new();
    private PointF? _down;

    public PatternChartView()
    {
        Drawable = _drawable;
        HeightRequest = 340;
        StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        EndInteraction += (_, e) =>
        {
            var up = e.Touches.FirstOrDefault();
            if (_down is { } down && e.IsInsideBounds && _drawable.Pattern is { Days.Count: > 0 } pattern && Math.Abs(up.X - down.X) < 16
                && up.X > PatternDrawable.Gutter)
            {
                var slot = (Width - PatternDrawable.Gutter) / pattern.Days.Count;
                var index = Math.Clamp((int)((up.X - PatternDrawable.Gutter) / slot), 0, pattern.Days.Count - 1);
                Tapped?.Invoke(this, pattern.Days[index].Evening);
            }

            _down = null;
        };
    }

    /// <summary>The day of the column that was tapped.</summary>
    public event EventHandler<DateOnly>? Tapped;

    /// <param name="labels">One label per column under it ("Mon", "12"); empty to leave a column unlabelled.</param>
    /// <param name="hours">The five hour labels down the side: noon, 6 PM, midnight, 6 AM, noon.</param>
    public void Show(SleepPattern pattern, IReadOnlyList<string> labels, IReadOnlyList<string> hours, bool feeds, Palette palette, bool diapers = false)
    {
        _drawable.Diapers = diapers;
        _drawable.Pattern = pattern;
        _drawable.Labels = labels;
        _drawable.Hours = hours;
        _drawable.Feeds = feeds;
        _drawable.Palette = palette;
        Invalidate();
    }

    private sealed class PatternDrawable : IDrawable
    {
        public const float Gutter = 44;
        private const float Top = 6, LabelHeight = 22;

        public SleepPattern? Pattern { get; set; }

        public IReadOnlyList<string> Labels { get; set; } = [];

        public IReadOnlyList<string> Hours { get; set; } = [];

        public bool Feeds { get; set; } = true;

        public bool Diapers { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (Pattern is not { Days.Count: > 0 } pattern)
                return;

            Color C(string hex) => Color.FromArgb(hex);
            var left = rect.X + Gutter;
            var bottom = rect.Bottom - LabelHeight;
            var height = bottom - Top;
            float Y(double fraction) => Top + (float)(fraction * height);

            // The hours down the side, with a faint line across at each.
            canvas.FontSize = 10;
            for (var h = 0; h < Hours.Count; h++)
            {
                var y = Y(h / (double)(Hours.Count - 1));
                canvas.StrokeColor = C(Palette.Line).WithAlpha(0.7f);
                canvas.StrokeSize = 1;
                canvas.DrawLine(left, y, rect.Right, y);
                canvas.FontColor = C(Palette.Ink2);
                canvas.DrawString(Hours[h], rect.X, y - 7, Gutter - 6, 14, HorizontalAlignment.Right, VerticalAlignment.Center);
            }

            var slot = (rect.Right - left) / pattern.Days.Count;
            var barWidth = Math.Clamp(slot * 0.7f, 3, 22);
            var radius = Math.Min(barWidth / 2, 5);
            for (var i = 0; i < pattern.Days.Count; i++)
            {
                var day = pattern.Days[i];
                var centre = left + (slot * (i + 0.5f));
                var x = centre - (barWidth / 2);
                if (day.Logged)
                {
                    // The column's track, so a logged day reads as a day even where she was awake.
                    canvas.FillColor = C(Palette.SleepTint).WithAlpha(0.35f);
                    canvas.FillRoundedRectangle(new RectF(x, Top, barWidth, height), radius);
                }

                foreach (var sleep in day.Sleeps)
                {
                    var top = Y(sleep.From);
                    var length = Math.Max(2, Y(sleep.To) - top);
                    canvas.FillColor = C(Palette.SleepMark).WithAlpha(sleep.IsLongest ? 1f : 0.5f);
                    canvas.FillRoundedRectangle(new RectF(x, top, barWidth, length), Math.Min(radius, length / 2));
                }

                if (Feeds)
                {
                    canvas.StrokeColor = C(Palette.FeedMark);
                    canvas.StrokeSize = 2;
                    var tick = Math.Min(slot * 0.9f, barWidth + 6);
                    foreach (var feed in day.Feeds)
                        canvas.DrawLine(centre - (tick / 2), Y(feed), centre + (tick / 2), Y(feed));
                }

                if (Diapers && day.Diapers is { Count: > 0 } changes)
                {
                    canvas.FillColor = C(Palette.DiaperMark);
                    foreach (var change in changes)
                        canvas.FillCircle(x + barWidth + Math.Min(4, (slot - barWidth) / 2), Y(change), 2.2f);
                }

                if (i < Labels.Count && Labels[i].Length > 0)
                {
                    canvas.FontColor = C(i == pattern.Days.Count - 1 ? Palette.Ink : Palette.Ink2);
                    canvas.FontSize = 10;
                    canvas.DrawString(Labels[i], centre - 20, bottom + 4, 40, LabelHeight - 4, HorizontalAlignment.Center, VerticalAlignment.Top);
                }
            }

            // When the longest sleep usually starts, across all the days.
            if (pattern.UsualLongestStart is { } usual)
            {
                var fromNoon = ((((usual.Hour * 60) + usual.Minute) - (12 * 60)) + (24 * 60)) % (24 * 60);
                var y = Y(fromNoon / (24.0 * 60));
                canvas.StrokeColor = C(Palette.SleepInk).WithAlpha(0.9f);
                canvas.StrokeSize = 1.5f;
                canvas.StrokeDashPattern = [5, 4];
                canvas.DrawLine(left, y, rect.Right, y);
                canvas.StrokeDashPattern = null;
            }
        }
    }
}
