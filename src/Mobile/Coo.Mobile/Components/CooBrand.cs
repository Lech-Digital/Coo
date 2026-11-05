using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Components;

/// <summary>The "mochi" wordmark: "moch" in ink and the "i" in the accent, or in gold with "Premium".</summary>
public static class CooBrand
{
    public static readonly Color DayGold = Color.FromArgb("#C5921D");
    public static readonly Color NightGold = Color.FromArgb("#F3C64F");

    public static Color Gold(bool isNight) => isNight ? NightGold : DayGold;

    public static View CreateMark(bool isPremium, bool isNight, Action? longPress = null, double fontSize = 24)
    {
        var formatted = new FormattedString();
        var goldColor = Gold(isNight);

        if (isPremium)
        {
            formatted.Spans.Add(new Span { Text = "moch", FontFamily = "FigtreeBold", FontSize = fontSize }
                .Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Ink")));
            formatted.Spans.Add(new Span { Text = "i", FontFamily = "FigtreeBold", FontSize = fontSize, TextColor = goldColor });
            formatted.Spans.Add(new Span { Text = " Premium", FontFamily = "FigtreeBold", FontSize = fontSize, TextColor = goldColor });
        }
        else
        {
            formatted.Spans.Add(new Span { Text = "moch", FontFamily = "FigtreeBold", FontSize = fontSize }
                .Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Ink")));
            formatted.Spans.Add(new Span { Text = "i", FontFamily = "FigtreeBold", FontSize = fontSize }
                .Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Accent")));
        }

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

        return label;
    }
}
