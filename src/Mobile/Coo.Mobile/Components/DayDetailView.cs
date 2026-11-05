using Coo.Domain.Common;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The zoomed day (Spec 009b §3): 6 hours per screen, swiped sideways, every hour labelled, each item labelled with
/// its time and length, the awake stretches named, a position bar for the whole day. A tap on an item reports its entry.
/// </summary>
public sealed class DayDetailView : ContentView
{
    private const double HoursPerScreen = 6;
    private readonly ScrollView _scroll = new() { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never };
    private readonly GraphicsView _canvas = new() { HeightRequest = 190 };
    private readonly GraphicsView _position = new() { HeightRequest = 6 };
    private readonly Label _title = Ui.Text("", "Section");
    private readonly Border _prevBtn;
    private readonly Border _nextBtn;
    private readonly DetailDrawable _drawable = new();
    private readonly PositionDrawable _bar = new();
    private Func<double, double, string> _rangeTitle = (_, _) => "";
    private double _openAt;
    private bool _scrollPending;
    private PointF? _down;
    private bool _navigatingDay;

    public Func<Task>? RequestPreviousDay { get; set; }
    public Func<Task>? RequestNextDay { get; set; }
    public Func<bool>? CanGoNext { get; set; }

    public DayDetailView()
    {
        _canvas.Drawable = _drawable;
        _position.Drawable = _bar;
        _scroll.Content = _canvas;
        _scroll.Scrolled += (_, _) => ShowRange();
        _scroll.SizeChanged += (_, _) => Fit();
        _canvas.SizeChanged += (_, _) => Fit();
        _canvas.StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        _canvas.EndInteraction += OnEndInteraction;

        var prevIcon = new Icon(IconGeometry.ChevronLeft, "Ink", 20) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _prevBtn = Ui.Rounded(prevIcon, "Surface", 999, new Thickness(10), "Line");
        _prevBtn.WidthRequest = 44;
        _prevBtn.HeightRequest = 44;
        _prevBtn.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = GoPreviousDayAsync()) });

        var nextIcon = new Icon(IconGeometry.ChevronRight, "Ink", 20) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _nextBtn = Ui.Rounded(nextIcon, "Surface", 999, new Thickness(10), "Line");
        _nextBtn.WidthRequest = 44;
        _nextBtn.HeightRequest = 44;
        _nextBtn.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = GoNextDayAsync()) });

        _title.HorizontalTextAlignment = TextAlignment.Center;
        _title.VerticalOptions = LayoutOptions.Center;

        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8,
            VerticalOptions = LayoutOptions.Center,
        };
        header.Add(_prevBtn, 0);
        header.Add(_title, 1);
        header.Add(_nextBtn, 2);

        Content = new VerticalStackLayout { Spacing = 12, Children = { header, _scroll, _position } };
    }

    /// <summary>An item was tapped: its entry.</summary>
    public event EventHandler<EntryId>? EntryTapped;

    public async Task GoPreviousDayAsync()
    {
        if (_navigatingDay || RequestPreviousDay is null)
            return;
        try
        {
            _navigatingDay = true;
            await RequestPreviousDay();
        }
        finally
        {
            _navigatingDay = false;
        }
    }

    public async Task GoNextDayAsync()
    {
        if (_navigatingDay || RequestNextDay is null || CanGoNext?.Invoke() != true)
            return;
        try
        {
            _navigatingDay = true;
            await RequestNextDay();
        }
        finally
        {
            _navigatingDay = false;
        }
    }

    public void ScrollToEnd()
    {
        if (_scroll.Width <= 0 || CanvasWidth <= 0)
            return;
        var x = Math.Max(0, CanvasWidth - _scroll.Width);
        Dispatcher.Dispatch(async () =>
        {
            await _scroll.ScrollToAsync(x, 0, animated: false);
            ShowRange();
        });
    }

    public void ScrollToStart()
    {
        Dispatcher.Dispatch(async () =>
        {
            await _scroll.ScrollToAsync(0, 0, animated: false);
            ShowRange();
        });
    }

    public void Show(IReadOnlyList<ChartItem> items, IReadOnlyList<HourTick> hours, double? now, Palette palette, Func<double, double, string> rangeTitle, double openAt)
    {
        _rangeTitle = rangeTitle;
        _openAt = DayChart.NearestHour(hours, openAt);
        _scrollPending = true;
        Update(items, hours, now, palette);
        Fit();
    }

    /// <summary>New data, colours or language while open: redraw where the parent already is, title included.</summary>
    public void Update(IReadOnlyList<ChartItem> items, IReadOnlyList<HourTick> hours, double? now, Palette palette)
    {
        _drawable.Items = items;
        _drawable.Hours = hours;
        _drawable.Now = now;
        _drawable.Palette = palette;
        _bar.Palette = palette;
        _canvas.Invalidate();
        ShowRange();
    }

    private double CanvasWidth => _scroll.Width * 24 / HoursPerScreen;

    // The canvas is 4× the viewport; on the first layout after Show, the tapped time sits in the middle, 3 hours either
    // side, and the day's start or end stops it at midnight (owner, 2026-10-02).
    private void Fit()
    {
        if (_scroll.Width <= 0)
            return;
        if (Math.Abs(_canvas.WidthRequest - CanvasWidth) > 0.5)
            _canvas.WidthRequest = CanvasWidth;
        // Scroll only once the canvas has its new width: before that, the scroll is clamped to 0 (found on the Pixel 10).
        if (!_scrollPending || Math.Abs(_canvas.Width - CanvasWidth) > 1)
            return;
        _scrollPending = false;
        var x = Math.Clamp(_openAt * CanvasWidth - _scroll.Width / 2, 0, CanvasWidth - _scroll.Width);
        Dispatcher.Dispatch(async () =>
        {
            await _scroll.ScrollToAsync(x, 0, animated: false);
            ShowRange();
        });
    }

    private void ShowRange()
    {
        if (CanvasWidth <= 0)
            return;
        var from = _scroll.ScrollX / CanvasWidth;
        var to = Math.Min(1, (_scroll.ScrollX + _scroll.Width) / CanvasWidth);
        _title.Text = _rangeTitle(from, to);
        _bar.From = from;
        _bar.To = to;
        _position.Invalidate();

        var canNext = CanGoNext?.Invoke() ?? false;
        _nextBtn.Opacity = canNext ? 1.0 : 0.35;
        _nextBtn.IsEnabled = canNext;
    }

    private async void OnEndInteraction(object? sender, TouchEventArgs e)
    {
        var up = e.Touches.FirstOrDefault();
        var down = _down;
        _down = null;
        if (down is null || !e.IsInsideBounds || CanvasWidth <= 0)
            return;

        var deltaX = up.X - down.Value.X;

        // If at the left edge and dragged right (swiped towards earlier time/yesterday)
        if (_scroll.ScrollX <= 12 && deltaX > 40)
        {
            await GoPreviousDayAsync();
            return;
        }

        // If at the right edge and dragged left (swiped towards later time/tomorrow)
        if (_scroll.ScrollX >= CanvasWidth - _scroll.Width - 12 && deltaX < -40)
        {
            if (CanGoNext?.Invoke() == true)
            {
                await GoNextDayAsync();
                return;
            }
        }

        // A hit area at least 24 dp wide (Spec 009b §3): 12 dp either side of the finger.
        if (Math.Abs(deltaX) <= 12)
        {
            if (DayChart.HitTest(_drawable.Items, up.X / CanvasWidth, 12 / CanvasWidth) is { EntryId: { } id })
                EntryTapped?.Invoke(this, id);
        }
    }

    private sealed class DetailDrawable : IDrawable
    {
        private const float Top = 76, BandHeight = 40;

        public IReadOnlyList<ChartItem> Items { get; set; } = [];

        public IReadOnlyList<HourTick> Hours { get; set; } = [];

        public double? Now { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            float X(double fraction) => rect.X + (float)(fraction * rect.Width);
            Color C(string hex) => Color.FromArgb(hex);
            canvas.FontSize = 11;

            foreach (var tick in Hours)
            {
                canvas.StrokeColor = C(Palette.Line);
                canvas.StrokeSize = 1;
                canvas.DrawLine(X(tick.At), 20, X(tick.At), 160);
                canvas.FontColor = C(Palette.Ink2);
                // The day's first and last labels stay inside the canvas.
                var (boxX, align) = tick.At <= 0 ? (X(tick.At), HorizontalAlignment.Left)
                    : tick.At >= 1 ? (X(tick.At) - 60, HorizontalAlignment.Right)
                    : (X(tick.At) - 30, HorizontalAlignment.Center);
                canvas.DrawString(tick.Label, boxX, 166, 60, 16, align, VerticalAlignment.Top);
            }

            canvas.FillColor = C(Palette.Surface);
            canvas.FillRoundedRectangle(rect.X, Top, rect.Width, BandHeight, 12);
            canvas.StrokeColor = C(Palette.Line);
            canvas.DrawRoundedRectangle(rect.X, Top, rect.Width, BandHeight, 12);

            // Two label rows above the feeds; below, one row for diapers and one for pumps, so a diaper and a pump at
            // the same time no longer sit on top of each other (2026-11-05). A label that would overlap is left out.
            var above = new[] { float.MinValue, float.MinValue };
            var below = new[] { float.MinValue, float.MinValue };
            foreach (var item in Items)
            {
                var x = X(item.From);
                var w = Math.Max(0, X(item.To) - x);
                var textWidth = canvas.GetStringSize(item.Label, Microsoft.Maui.Graphics.Font.Default, 11).Width;
                switch (item.Shape)
                {
                    case ChartShape.Sleep:
                        canvas.FillColor = C(Palette.SleepMark).WithAlpha(0.85f);
                        canvas.FillRoundedRectangle(x + 0.5f, Top + 3, Math.Max(3, w - 1), BandHeight - 6, 9);
                        if (textWidth <= w - 8)
                        {
                            canvas.FontColor = C(Palette.Surface);
                            canvas.DrawString(item.Label, x + 4, Top + 12, w - 8, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                        }
                        else if (w >= 16)
                        {
                            canvas.FontColor = C(Palette.Surface);
                            canvas.DrawString(item.Label, x + 2, Top + 12, textWidth + 4, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                        }

                        break;
                    case ChartShape.Awake:
                        if (textWidth <= w - 12)
                        {
                            canvas.FontColor = C(Palette.Ink2);
                            canvas.DrawString(item.Label, x + 6, Top + 12, w - 12, 16, HorizontalAlignment.Center, VerticalAlignment.Top);
                        }

                        break;
                    case ChartShape.Feed:
                        canvas.FillColor = C(Palette.FeedMark);
                        canvas.FillRoundedRectangle(x, Top - 18, Math.Max(7, w), 12, 6);
                        // A feed in parts is labelled at its first part only (Spec 011d §5).
                        var row = item.Label.Length == 0 ? -1 : x >= above[0] + 4 ? 0 : x >= above[1] + 4 ? 1 : -1;
                        if (row >= 0)
                        {
                            canvas.FontColor = C(Palette.FeedInk);
                            canvas.DrawString(item.Label, x, Top - 36 - (row * 16), textWidth + 2, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                            above[row] = x + textWidth;
                        }

                        break;
                    case ChartShape.Diaper:
                    case ChartShape.Pump:
                        var diaper = item.Shape == ChartShape.Diaper;
                        var lane = diaper ? 0 : 1;
                        var y = Top + BandHeight + 12 + (lane * 20);
                        canvas.FillColor = C(diaper ? Palette.DiaperMark : Palette.PumpMark);
                        if (diaper)
                            canvas.FillCircle(x, y, 5);
                        else
                            canvas.FillRoundedRectangle(x - 5, y - 5, 10, 10, 3);
                        if (x - 4 >= below[lane] + 4)
                        {
                            canvas.FontColor = C(diaper ? Palette.DiaperInk : Palette.PumpInk);
                            canvas.DrawString(item.Label, x + 9, y - 7, textWidth + 2, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                            below[lane] = x + 9 + textWidth;
                        }

                        break;
                }
            }

            if (Now is { } now)
            {
                canvas.StrokeColor = C(Palette.Accent);
                canvas.StrokeSize = 2;
                canvas.DrawLine(X(now), 20, X(now), 160);
            }
        }
    }

    private sealed class PositionDrawable : IDrawable
    {
        public double From { get; set; }

        public double To { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.FillColor = Color.FromArgb(Palette.Line);
            canvas.FillRoundedRectangle(rect, 3);
            canvas.FillColor = Color.FromArgb(Palette.Ink2);
            canvas.FillRoundedRectangle(rect.X + (float)(From * rect.Width), rect.Y, (float)((To - From) * rect.Width), rect.Height, 3);
        }
    }
}
