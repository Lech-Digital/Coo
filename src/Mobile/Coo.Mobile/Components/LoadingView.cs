using Microsoft.Maui.Controls.Shapes;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// Coo's full-page wait (owner, 2026-11-03: signing in, a purchase or a switch jumped through several screens): the mark
/// breathing in the middle, three dots in turn under it, and one line on what is happening. In the page's colours, so it
/// follows night mode. The animation runs while the view is on screen.
/// </summary>
public sealed class LoadingView : Grid
{
    private readonly View _mark;
    private readonly Ellipse[] _dots;
    private readonly Label _message;
    private bool _running;

    public LoadingView(string message, bool isNight)
    {
        SetDynamicResource(BackgroundColorProperty, "Ground");
        _mark = CooBrand.CreateMark(false, isNight, fontSize: 52);
        _mark.HorizontalOptions = LayoutOptions.Center;

        _dots = [.. Enumerable.Range(0, 3).Select(_ =>
        {
            var dot = new Ellipse { WidthRequest = 10, HeightRequest = 10, Opacity = 0.25 };
            dot.SetDynamicResource(Shape.FillProperty, "AccentBrush");
            return dot;
        })];
        var dots = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.Center };
        foreach (var dot in _dots)
            dots.Add(dot);

        _message = Ui.Text(message, "Body", "Ink2");
        _message.HorizontalTextAlignment = TextAlignment.Center;

        Children.Add(new VerticalStackLayout
        {
            Spacing = 24,
            Padding = new Thickness(32, 0),
            VerticalOptions = LayoutOptions.Center,
            Children = { _mark, dots, _message },
        });
        SemanticProperties.SetDescription(this, message);

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _running = false;
    }

    public string Message
    {
        get => _message.Text;
        set
        {
            _message.Text = value;
            SemanticProperties.SetDescription(this, value);
        }
    }

    private void Start()
    {
        if (_running)
            return;
        _running = true;
        Dispatcher.Dispatch(async () =>
        {
            var step = 0;
            while (_running)
            {
                // The mark breathes once per cycle; the dots light one after the other.
                var breathe = _mark.ScaleToAsync(step % 2 == 0 ? 1.06 : 1.0, 600, Easing.SinInOut);
                for (var i = 0; i < _dots.Length; i++)
                {
                    var on = i == step % _dots.Length;
                    _ = _dots[i].FadeToAsync(on ? 1 : 0.25, 300, Easing.SinInOut);
                }

                await breathe;
                step++;
            }
        });
    }
}

/// <summary>Shows <see cref="LoadingView"/> as a full page over the app while some work runs.</summary>
public static class LoadingOverlay
{
    /// <summary>Long enough to read, so a quick answer does not flash the screen.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(900);

    public static async Task RunAsync(string message, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
        var navigation = windows is { Count: > 0 } ? windows[0].Page?.Navigation : null;
        if (navigation is null)
        {
            await work();
            return;
        }

        var night = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services.GetService<ThemeController>()?.Current == PaletteKind.Night;
        var page = new ContentPage { Content = new LoadingView(message, night) };
        page.SetDynamicResource(VisualElement.BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(page, false);
        await navigation.PushModalAsync(page, animated: false);
        var shown = Task.Delay(Minimum);
        try
        {
            await work();
            await shown;
        }
        finally
        {
            if (navigation.ModalStack.Count > 0 && ReferenceEquals(navigation.ModalStack[^1], page))
                await navigation.PopModalAsync(animated: false);
        }
    }
}
