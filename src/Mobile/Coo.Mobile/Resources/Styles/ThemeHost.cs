using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Resources.Styles;

/// <summary>
/// Applies the palette the <see cref="ThemeController"/> picks (Spec 005 §6.2): it swaps the merged colour dictionary,
/// built from <see cref="WashiPalette"/>, and repaints the Android system bars. It also reports Android's dark mode.
/// </summary>
public sealed class ThemeHost : IOsAppearance
{
    private ResourceDictionary? _current;

    public ThemeHost()
    {
        if (Microsoft.Maui.Controls.Application.Current is { } app)
            app.RequestedThemeChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public bool IsDark => Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark;

    /// <summary>The token dictionary for a palette: one Color resource per token, keyed by the token's name.</summary>
    public static ResourceDictionary Build(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var dictionary = new ResourceDictionary();
        foreach (var (key, hex) in palette.Tokens)
        {
            var colour = Color.FromArgb(hex);
            dictionary[key] = colour;

            // Shapes stroke with a Brush, not a Color: every token also exists as "<Token>Brush".
            dictionary[key + "Brush"] = new SolidColorBrush(colour);
        }

        // The wash behind the pages, the glass cards, the sheet's veil and the Premium tag (owner, 2026-11-05).
        foreach (var (key, value) in Coo.Mobile.Components.WashResources.For(palette.Kind))
            dictionary[key] = value;
        return dictionary;
    }

    /// <summary>
    /// A palette change while Coo is on screen (Spec 009 §3.7, design system §7): the window behind the page takes the
    /// new ground colour, the page fades out, the colours swap unseen, and the page fades back in. Repainting every
    /// view takes a moment; the fade hides it (owner's finding on the Pixel 10).
    /// </summary>
    public async Task ApplyAnimatedAsync(PaletteKind kind)
    {
        var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
        var page = windows is { Count: > 0 } ? windows[0].Page : null;
        if (page is null || page.Opacity < 1)
        {
            Apply(kind);
            return;
        }

        SystemBars.Apply(Color.FromArgb(WashiPalette.For(kind).Ground), lightContent: kind == PaletteKind.Night);
        await page.FadeToAsync(0, 120, Easing.CubicIn);
        Apply(kind);
        await page.FadeToAsync(1, 180, Easing.CubicOut);
    }

    public void Apply(PaletteKind kind)
    {
        var resources = Microsoft.Maui.Controls.Application.Current?.Resources
            ?? throw new InvalidOperationException("The application has no resources yet.");
        var next = Build(WashiPalette.For(kind));
        if (_current is not null)
            resources.MergedDictionaries.Remove(_current);
        resources.MergedDictionaries.Add(next);
        _current = next;
        SystemBars.Apply(Color.FromArgb(WashiPalette.For(kind).Ground), lightContent: kind == PaletteKind.Night);
    }
}
