using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Components;

/// <summary>
/// Preset avatar definition with its key, SVG image resource name, and friendly label.
/// </summary>
public sealed record AvatarPreset(string Key, string ImageResource, string Name, string Gender);

/// <summary>
/// A shared circular avatar component for rendering baby avatars across Coo:
/// - Preset SVG illustrated babies (boys and girls across skin tones and hair colors)
/// - Local photo image file (from camera, gallery or AI generation)
/// - Default monogram (initial letter centered with Cheek styling)
/// </summary>
public static class CooAvatar
{
    public static readonly IReadOnlyList<AvatarPreset> Presets =
    [
        new("boy_fair_brown", "avatar_boy_fair_brown.png", "Boy · Fair / Brown hair", "Boy"),
        new("girl_fair_brown", "avatar_girl_fair_brown.png", "Girl · Fair / Brown hair", "Girl"),
        new("boy_fair_blonde", "avatar_boy_fair_blonde.png", "Boy · Fair / Blonde hair", "Boy"),
        new("girl_fair_ginger", "avatar_girl_fair_ginger.png", "Girl · Fair / Ginger hair", "Girl"),
        new("boy_medium_dark", "avatar_boy_medium_dark.png", "Boy · Medium / Dark hair", "Boy"),
        new("girl_medium_dark", "avatar_girl_medium_dark.png", "Girl · Medium / Dark hair", "Girl"),
        new("boy_dark_curls", "avatar_boy_dark_curls.png", "Boy · Deep / Curls", "Boy"),
        new("girl_dark_puffs", "avatar_girl_dark_puffs.png", "Girl · Deep / Afro puffs", "Girl"),
    ];

    public static View Create(string? avatarRef, string initialText, double size = 28)
    {
        if (!string.IsNullOrEmpty(avatarRef))
        {
            if (avatarRef.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            {
                var key = avatarRef["preset:".Length..];
                var preset = Presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                    ?? Presets[0];

                var img = new Image
                {
                    Source = preset.ImageResource,
                    Aspect = Aspect.AspectFill,
                    WidthRequest = size,
                    HeightRequest = size,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                };

                var border = new Border
                {
                    Content = img,
                    Padding = 0,
                    WidthRequest = size,
                    HeightRequest = size,
                    StrokeShape = new RoundRectangle { CornerRadius = size / 2 },
                    StrokeThickness = 0,
                };
                return border;
            }

            if (avatarRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var path = avatarRef["file:".Length..];
                if (File.Exists(path))
                {
                    var img = new Image
                    {
                        Source = ImageSource.FromFile(path),
                        Aspect = Aspect.AspectFill,
                        WidthRequest = size,
                        HeightRequest = size,
                        HorizontalOptions = LayoutOptions.Fill,
                        VerticalOptions = LayoutOptions.Fill,
                    };

                    var border = new Border
                    {
                        Content = img,
                        Padding = 0,
                        WidthRequest = size,
                        HeightRequest = size,
                        StrokeShape = new RoundRectangle { CornerRadius = size / 2 },
                        StrokeThickness = 1,
                    };
                    border.SetDynamicResource(Border.StrokeProperty, "LineBrush");
                    return border;
                }
            }
        }

        // Monogram fallback
        var initialLabel = new Label
        {
            Text = initialText,
            FontFamily = "FigtreeBold",
            FontSize = size * 0.46,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        initialLabel.SetDynamicResource(Label.TextColorProperty, "CheekInk");

        var monogramGrid = new Grid
        {
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { initialLabel }
        };

        var monogram = Ui.Rounded(monogramGrid, "Cheek", 999, new Thickness(0));
        monogram.WidthRequest = monogram.HeightRequest = size;
        return monogram;
    }
}
