using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Components;

/// <summary>
/// A bottom sheet as an in-page overlay (Spec 006 §2), never a modal page: it shares the page's window, so the system
/// bars keep the palette. It closes on a tap outside, on Back (the page asks), or by its owner.
/// </summary>
public sealed class Sheet : Grid
{
    private readonly Border _panel;
    private readonly BoxView _backdrop;

    /// <summary>The scroll view the sheet put around its content, if it made one.</summary>
    private ScrollView? _wrapper;

    /// <summary>Counts the times it was shown, so a close that a new show overtook does not hide it.</summary>
    private int _generation;

    private bool _closing;

    public Sheet()
    {
        IsVisible = false;


        _backdrop = new BoxView { Opacity = 0 };
        _backdrop.SetDynamicResource(BoxView.ColorProperty, "Scrim");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _backdrop.GestureRecognizers.Add(tap);

        _panel = new Border
        {
            VerticalOptions = LayoutOptions.End,

            // Never up to the very top: the page shows above it, so it reads as a sheet.
            Margin = new Thickness(0, 48, 0, 0),
            StrokeThickness = 0,
            Padding = new Thickness(24, 20, 24, 32),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28, 28, 0, 0) },
        };
        _panel.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        _panel.GestureRecognizers.Add(new TapGestureRecognizer());
        Children.Add(_backdrop);
        Children.Add(_panel);
    }

    /// <summary>The user dismissed the sheet; the owner decides what closing means.</summary>
    public event EventHandler? Closed;

    public async Task ShowAsync(View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        // Scrolls when taller than the screen: Earlier… with every kind shown cut off Solid food and Milestone (seen on the
        // emulator, 2026-10-27).
        // Start, not Fill: a filling ScrollView took the whole screen height, and Feed and Bottle showed a page of empty
        // sheet under three buttons (owner, 2026-11-03). Now the sheet is as tall as what it holds, up to the margin.
        // The same content can come back (the Log's zoomed day): it keeps its place when it is already showing, and the
        // scroll view that held it lets go first, as Android throws "the specified child already has a parent" when a
        // view joins a second one (owner, 2026-11-06: tapping the Log's timeline crashed the app).
        var showing = ReferenceEquals(_panel.Content, content) || (_wrapper is not null && ReferenceEquals(_wrapper.Content, content));
        if (!showing)
        {
            Release();
            if (content is ScrollView own)
            {
                _panel.Content = own;
            }
            else
            {
                if (content.Parent is ScrollView holder)
                    holder.Content = null;
                Detach(content);
                _wrapper = new ScrollView { Content = content, VerticalOptions = LayoutOptions.Start };
                _panel.Content = _wrapper;
            }
        }

        _generation++;
        if (IsVisible && !_closing)
            return;
        var fromHidden = !IsVisible;
        _closing = false;
        IsVisible = true;
        if (fromHidden)
            _panel.TranslationY = 400;
        // Lighter at night, so the wash still shows around the drawer (owner, 2026-11-05).
        var veil = Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue("ScrimOpacity", out var o) == true && o is double d ? d : 0.35;
        await Task.WhenAll(_backdrop.FadeToAsync(veil, 160), _panel.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    public async Task HideAsync()
    {
        if (!IsVisible || _closing)
            return;
        _closing = true;
        var generation = _generation;
        await Task.WhenAll(_backdrop.FadeToAsync(0, 140), _panel.TranslateToAsync(0, 400, 160, Easing.CubicIn));
        // Shown again while it was closing: it stays, with what it now shows.
        if (generation != _generation)
            return;
        _closing = false;
        IsVisible = false;
        Release();
    }

    /// <summary>Empties the panel, and the scroll view the sheet made, so what it held can be shown again.</summary>
    private void Release()
    {
        if (_wrapper is not null)
        {
            var held = _wrapper.Content;
            _wrapper.Content = null;
            if (held is not null)
                Detach(held);
            _wrapper = null;
        }

        _panel.Content = null;
    }

    /// <summary>
    /// Takes the view's Android view out of the container it is in. Emptying a ScrollView leaves its content's Android
    /// view inside the old scroll view's inner container, so showing it again threw "the specified child already has a
    /// parent" (owner, 2026-11-06).
    /// </summary>
    private static void Detach(View view)
    {
#if ANDROID
        if (view.Handler?.PlatformView is Android.Views.View platform && platform.Parent is Android.Views.ViewGroup parent)
            parent.RemoveView(platform);
#endif
    }
}
