using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Components;

/// <summary>The "mochi" wordmark: "moch" in ink and the "i" in the accent; with Premium, a gold PREMIUM tag beside it.</summary>
public static class CooBrand
{
    public static readonly Color DayGold = Color.FromArgb("#C5921D");
    public static readonly Color NightGold = Color.FromArgb("#F3C64F");

    public static Color Gold(bool isNight) => isNight ? NightGold : DayGold;

    public static View CreateMark(bool isPremium, bool isNight, Action? longPress = null, double fontSize = 24)
    {
        // "moch" in ink and the "i" in the accent; Premium adds a small gold PREMIUM tag beside it (owner, 2026-11-05).
        var formatted = new FormattedString();
        formatted.Spans.Add(new Span { Text = "moch", FontFamily = "FigtreeBold", FontSize = fontSize }
            .Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Ink")));
        formatted.Spans.Add(new Span { Text = "i", FontFamily = "FigtreeBold", FontSize = fontSize }
            .Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Accent")));

        var label = new Label
        {
            FormattedText = formatted,
            VerticalOptions = LayoutOptions.Center,
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
