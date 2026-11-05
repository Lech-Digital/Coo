using System.Globalization;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.DebugScreen;

/// <summary>
/// The style gallery (Spec 005 §9): every token, type style, icon and base component, in the live palette, to check
/// against <c>docs/design/washi-ux.html</c>. Debug and <c>-p:CooDebugScreen=true</c> builds only.
/// </summary>
public sealed class GalleryPage : ContentPage
{
    private static readonly string[] Weights = ["Light", "Regular", "Medium", "SemiBold", "Bold"];

    private readonly ThemeController _theme;
    private readonly TimeProvider _time;
    private readonly Presentation.Sheets.SheetNavigator _sheets;

    public GalleryPage(ThemeController theme, TimeProvider time, Presentation.Sheets.SheetNavigator sheets)
    {
        _theme = theme;
        _time = time;
        _sheets = sheets;
        Title = "Style gallery";
        _theme.PaletteChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Rebuild);
        Rebuild();
    }

    /// <summary>A sheet on the window's own stack (review M1): Back closes it through the navigator.</summary>
    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () =>
        {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape.
            try
            {
                await _sheets.CloseAsync(this);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        });
        return true;
    }

    private void Rebuild()
    {
        var palette = WashiPalette.For(_theme.Current);
        // Every view is created fresh on each rebuild: an Android view cannot be added to a second parent, and
        // reusing one across rebuilds crashed the app when the palette switched.
        var mode = Ui.Text($"Setting: {_theme.Settings.Mode} · showing {palette.Kind} · quiet hours {_theme.Settings.QuietHours.Start:HH\\:mm}–{_theme.Settings.QuietHours.End:HH\\:mm}", "Meta");

        var page = new VerticalStackLayout { Padding = new Thickness(24, 48, 24, 48), Spacing = 16 };
        page.Add(Ui.Text("Style gallery", "Title"));
        page.Add(mode);
        page.Add(Row(ModeButton("Auto", AppearanceMode.Auto), ModeButton("Day", AppearanceMode.Day), ModeButton("Night", AppearanceMode.Night)));

        page.Add(Section("Colour"));
        foreach (var (key, hex) in palette.Tokens)
            page.Add(Swatch(key, hex));
        page.Add(Ui.Text("Contrast", "BodyStrong"));
        foreach (var (fg, bg) in new[]
                 {
                     ("Ink", "Ground"), ("Ink2", "Ground"), ("OnAccent", "Accent"), ("AccentInk", "AccentSoft"), ("VoiceHint", "VoiceBg"),
                     ("FeedInk", "FeedTint"), ("DiaperInk", "DiaperTint"), ("SleepInk", "SleepTint"), ("PumpInk", "PumpTint"),
                     ("FeedMark", "Ground"), ("DiaperMark", "Ground"), ("SleepMark", "Ground"), ("PumpMark", "Ground"),
                 })
        {
            var ratio = ColourMath.ContrastRatio(palette.Tokens[fg], palette.Tokens[bg]);
            page.Add(Ui.Text($"{fg} on {bg}: {ratio:F2}:1", "Meta"));
        }

        page.Add(Section("Type"));
        foreach (var style in new[] { "Hero", "Window", "Title", "Section", "Body", "BodyStrong", "Meta", "Tile" })
            page.Add(Ui.Text(style == "Hero" ? "1h 20m" : style == "Window" ? "14:10 – 14:40" : $"{style} · Zażółć gęślą jaźń", style));
        page.Add(Ui.Text("NEXT NAP WINDOW", "Label"));

        page.Add(Section("Tabular figures (Spec 005 §4.2)"));
        page.Add(Ui.Text("Each block: 1111 above 8888. Left: plain Figtree (it jitters). Right: Android tnum, the chosen approach (it must line up).", "Meta"));
        foreach (var weight in Weights)
            page.Add(Row(Digits($"Figtree{weight}", tnum: false), Digits($"Figtree{weight}", tnum: true)));
        page.Add(Ui.Text("Live timers, same three:", "Meta"));
        page.Add(Row(LiveTimer("FigtreeLight", false), LiveTimer("FigtreeLight", true)));

        page.Add(Section("Polish in every weight"));
        foreach (var weight in Weights)
        {
            page.Add(new Label { Text = "Zażółć gęślą jaźń · ZAŻÓŁĆ GĘŚLĄ JAŹŃ", FontFamily = $"Figtree{weight}", FontSize = 18 }
                .Tap(l => l.SetDynamicResource(Label.TextColorProperty, "Ink")));
        }

        page.Add(Section("Font scaling at 200%"));
        page.Add(new Label { Text = "Body text at twice the size wraps instead of clipping, even in German: Windelwechsel protokollieren", FontFamily = "FigtreeRegular", FontSize = 30, LineBreakMode = LineBreakMode.WordWrap }
            .Tap(l => l.SetDynamicResource(Label.TextColorProperty, "Ink")));

        page.Add(Section("Icons"));
        foreach (var colour in new[] { "Ink", "Ink2", "FeedInk", "DiaperInk", "SleepInk", "PumpInk" })
            page.Add(Row([.. IconGeometry.All.Values.Select(g => (View)new Icon(g, colour, 24))]));
        page.Add(Row([.. IconGeometry.All.Values.Take(6).Select(g => (View)new Icon(g, "Ink", 48))]));

        page.Add(Section("Components"));
        page.Add(Row(new Pill("High confidence"), new Pill("Low confidence")));
        page.Add(Row(Button("Log feed", "PrimaryButton"), Button("Cancel", "GhostButton")));
        page.Add(new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceBetween,
            Children =
            {
                new LogTile("feeding", "Feed", "12:40 · L"),
                new LogTile("diaper", "Diaper", "13:05"),
                new LogTile("sleep", "Sleep", "woke 12:25"),
                new LogTile("pump", "Pump", "09:15"),
            },
        });
        page.Add(new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            ColumnSpacing = 8,
        }.Tap(g =>
        {
            g.Add(new TotalsCard("feeding", "6", "feeds", "Breast 4 · bottle 2", "Last 9:52 · usually 7"), 0);
            g.Add(new TotalsCard("diaper", "5", "diapers", "Wet 4 · dirty 1", "Usually 6"), 1);
            g.Add(new TotalsCard("sleep", "9 h 40 m", "sleep", "Longest 3 h 10 m", ""), 2);
            g.Add(new TotalsCard("pump", "210", "ml pumped", "Sessions 2", ""), 3);
        }));
        page.Add(new Card(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new TimelineRow("13:05", "diaper", "Diaper · wet", "Naïma"),
                new TimelineRow("12:40", "feeding", "Breast · left · 14 min", "You"),
                new TimelineRow("11:10", "sleep", "Nap · 1 h 15 m", "Naïma"),
                new TimelineRow("09:15", "pump", "Pumping · 110 ml", "You"),
            },
        }));
        page.Add(new Card(new NightTimer("feeding", "Feeding · left", _time.GetLocalNow().AddMinutes(-12), _time, "Switch side")));

        Content = new ScrollView { Content = page };
    }

    private Button ModeButton(string text, AppearanceMode mode)
    {
        var button = Button(text, _theme.Settings.Mode == mode ? "PrimaryButton" : "GhostButton");
        button.Clicked += async (_, _) => await _theme.SetAsync(_theme.Settings with { Mode = mode }, CancellationToken.None);
        return button;
    }

    private static Button Button(string text, string style)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(VisualElement.StyleProperty, style);
        return button;
    }

    private static Label Section(string text) => Ui.Text(text, "Section").Tap(l => l.Margin = new Thickness(0, 16, 0, 0));

    private static HorizontalStackLayout Swatch(string key, string hex)
    {
        var chip = new BoxView { WidthRequest = 44, HeightRequest = 28, CornerRadius = 8 };
        chip.SetDynamicResource(BoxView.ColorProperty, key);
        return Row(chip, Ui.Text($"{key}  {hex}", "Meta").Tap(l => l.VerticalOptions = LayoutOptions.Center));
    }

    private static VerticalStackLayout Digits(string family, bool tnum)
    {
        Label Make(string text)
        {
            var label = new Label { Text = text, FontFamily = family, FontSize = 30 };
            label.SetDynamicResource(Label.TextColorProperty, "Ink");
            if (tnum)
                label.StyleClass = [Tabular.StyleClass];
            return label;
        }

        return new VerticalStackLayout { Spacing = 0, WidthRequest = 104, Children = { Make("1111"), Make("8888") } };
    }

    private Border LiveTimer(string family, bool tnum)
    {
        var label = new Label { FontFamily = family, FontSize = 30 };
        label.SetDynamicResource(Label.TextColorProperty, "Ink");
        if (tnum)
            label.StyleClass = [Tabular.StyleClass];
        var start = _time.GetUtcNow();
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(97);
        timer.Tick += (_, _) => label.Text = (_time.GetUtcNow() - start).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        timer.Start();
        return new Border { Content = label, WidthRequest = 104, StrokeThickness = 0 };
    }

    private static HorizontalStackLayout Row(params View[] views)
    {
        var row = new HorizontalStackLayout { Spacing = 8 };
        foreach (var view in views)
            row.Add(view);
        return row;
    }
}
