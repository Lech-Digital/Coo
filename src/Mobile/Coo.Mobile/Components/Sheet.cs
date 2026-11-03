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

    public Sheet()
    {
        IsVisible = false;


        _backdrop = new BoxView { Color = Colors.Black, Opacity = 0 };
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
        // Scrolls when taller than the screen: Earlier… with every kind shown cut off Solid food and Milestone (seen on the
        // emulator, 2026-10-27).
        // Start, not Fill: a filling ScrollView took the whole screen height, and Feed and Bottle showed a page of empty
        // sheet under three buttons (owner, 2026-11-03). Now the sheet is as tall as what it holds, up to the margin.
        _panel.Content = content as ScrollView ?? new ScrollView { Content = content, VerticalOptions = LayoutOptions.Start };
        if (IsVisible)
            return;
        IsVisible = true;
        _panel.TranslationY = 400;
        await Task.WhenAll(_backdrop.FadeToAsync(0.35, 160), _panel.TranslateToAsync(0, 0, 180, Easing.CubicOut));
    }

    public async Task HideAsync()
    {
        if (!IsVisible)
            return;
        await Task.WhenAll(_backdrop.FadeToAsync(0, 140), _panel.TranslateToAsync(0, 400, 160, Easing.CubicIn));
        IsVisible = false;
        _panel.Content = null;
    }
}
