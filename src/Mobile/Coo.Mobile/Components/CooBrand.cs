using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Components;

/// <summary>The "mochi" wordmark: "moch" in ink, the "i" in the accent with the bunny as its dot; with Premium, a gold PREMIUM tag beside it.</summary>
public static class CooBrand
{
    public static readonly Color DayGold = Color.FromArgb("#C5921D");
    public static readonly Color NightGold = Color.FromArgb("#F3C64F");

    public static Color Gold(bool isNight) => isNight ? NightGold : DayGold;

    /// <param name="fontSize">The size of the letters, as a font size (see <see cref="WordmarkView"/>).</param>
    public static View CreateMark(bool isPremium, bool isNight, Action? longPress = null, double fontSize = 24)
    {
        // Fredoka "moch" in ink, the "i" stem in the accent and the logo bunny as its dot (owner, 2026-11-06), drawn
        // from outlines on one canvas so the app needs no extra font and every part stands on the same baseline.
        var label = new WordmarkView(fontSize)
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
            InputTransparent = longPress == null,
        };

        if (longPress != null)
        {
            var burst = new TapBurst(TimeProvider.System);
            label.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    if (burst.Tap())
                        longPress();
                }),
            });
        }

        if (!isPremium)
            return label;

        var tagText = new Label { Text = "PREMIUM", FontFamily = "FigtreeBold", FontSize = Math.Round(fontSize * 0.42), CharacterSpacing = 1.4 };
        tagText.SetDynamicResource(Label.TextColorProperty, "PremiumTagInk");
        var tag = new Border
        {
            Content = tagText,
            Padding = new Thickness(7, 2),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 7 },
            VerticalOptions = LayoutOptions.Center,
        };
        tag.SetDynamicResource(Border.BackgroundProperty, "PremiumTag");
        return new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center, Children = { label, tag } };
    }
}
