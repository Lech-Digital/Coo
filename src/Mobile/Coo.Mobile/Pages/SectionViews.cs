using System.ComponentModel;
using Coo.Domain.Common;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Pages;

/// <summary>
/// The view for each <see cref="IHomeSection"/> (Spec 006 §3a). A new section type gets one more case here; an unknown
/// one renders as nothing rather than breaking Today. Each view re-renders when its section changes.
/// </summary>
/// <summary>What a section view can ask its page to do.</summary>
internal sealed record SectionHost(Action? OpenDebug, Action<EntryId> OpenEntry, Action OpenAttention, Action OpenEarlier, TodayViewModel Today);

internal static class SectionViews
{
    public static View Create(IHomeSection section, SectionHost host) => section switch
    {
        BabyHeaderSection s => Live(s, () => Header(s, host)),
        DueDateSection s => Live(s, () => DueDate(s)),
        StatusHeroSection s => Live(s, () => Hero(s)),
        RunningSection s => Live(s, () => RunningCards(s.Items, night: false, host)),
        AttentionSection s => Live(s, () => Attention(s, host)),
        LogTilesSection s => Live(s, () => Tiles(s, host)),
        TimelineSection s => Live(s, () => Timeline(s, host)),
        NightSection s => Live(s, () => Night(s, host), nameof(NightSection.Title)),
        _ => new ContentView(),
    };

    /// <summary>
    /// Re-renders the content when the section raises PropertyChanged, at most once per UI frame. Properties listed in
    /// <paramref name="boundInPlace"/> are bound to their labels instead, so they never rebuild the view.
    /// </summary>
    private static ContentView Live(INotifyPropertyChanged section, Func<View> render, params string[] boundInPlace)
    {
        var view = new ContentView { Content = render() };
        var pending = false;
        section.PropertyChanged += (_, e) =>
        {
            if (pending || boundInPlace.Contains(e.PropertyName))
                return;
            pending = true;
            view.Dispatcher.Dispatch(() =>
            {
                pending = false;
                view.Content = render();
            });
        };
        return view;
    }

    /// <summary>
    /// The Today switch (Spec 009 §3.7): a moon by day, a sun by night, 44 dp to tap. It redraws when the palette
    /// changes, because a section that shows in both palettes is not rebuilt then.
    /// </summary>
    private static View ThemeSwitch(SectionHost host)
    {
        var view = new ContentView { WidthRequest = 44, HeightRequest = 44, VerticalOptions = LayoutOptions.Center };
        void Render()
        {
            var night = host.Today.IsNight;
            view.Content = new Icon(night ? IconGeometry.Sun : IconGeometry.Sleep, "Ink2")
                .Tap(i => { i.HorizontalOptions = LayoutOptions.Center; i.VerticalOptions = LayoutOptions.Center; });
            SemanticProperties.SetDescription(view, Strings.Get(night ? "Theme.ToDay" : "Theme.ToNight"));
        }

        host.Today.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TodayViewModel.IsNight))
                view.Dispatcher.Dispatch(Render);
        };
        var tap = new TapGestureRecognizer { Command = host.Today.ToggleThemeCommand };
        view.GestureRecognizers.Add(tap);
        Render();
        return view;
    }

    private static View Header(BabyHeaderSection s, SectionHost host)
    {
        var longPress = host.OpenDebug;
        var mark = new Label
        {
            FormattedText = new FormattedString
            {
                Spans =
                {
                    new Span { Text = "co", FontFamily = "FigtreeBold", FontSize = 24 }.Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Ink")),
                    new Span { Text = "o", FontFamily = "FigtreeBold", FontSize = 24 }.Tap(x => x.SetDynamicResource(Span.TextColorProperty, "Accent")),
                },
            },
            VerticalOptions = LayoutOptions.Center,
        };
        AttachLongPress(mark, longPress);

        var initial = Ui.Rounded(Ui.Text(s.Initial, "Meta", "CheekInk").Tap(l => { l.FontFamily = "FigtreeBold"; l.HorizontalOptions = LayoutOptions.Center; }), "Cheek", 999, new Thickness(0));
        initial.WidthRequest = 28;
        initial.HeightRequest = 28;
        var chip = Ui.Rounded(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { initial, Ui.Text($"{s.BabyName} · {s.Age}", "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center) },
        }, "Surface", 999, new Thickness(6, 6, 14, 6), "Line");

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 4, Padding = new Thickness(24, 16, 24, 8) };
        grid.Add(mark, 0);
        grid.Add(ThemeSwitch(host), 1);
        grid.Add(chip, 2);
        return grid;
    }

    private static View DueDate(DueDateSection s)
    {
        var arrived = new Button { Text = Strings.Get("Due.Arrived"), Command = s.ArrivedCommand, Margin = new Thickness(0, 12, 0, 0) };
        arrived.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        return new Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Text(Strings.Get("Expecting.Eyebrow").ToUpper(Strings.Culture), "Label", "SleepInk"),
                Ui.Text(s.Headline, "Title"),
                Ui.Text(s.DateText, "Body", "Ink2"),
                arrived,
            },
        }).Tap(c => c.Margin = new Thickness(24, 16, 24, 0));
    }

    private static View Hero(StatusHeroSection s) => new VerticalStackLayout
    {
        Padding = new Thickness(24, 20, 24, 4),
        Spacing = 6,
        Children =
        {
            Ui.Text(s.Eyebrow.ToUpper(Strings.Culture), "Label"),
            Ui.Text(s.Value, s.Eyebrow.Length == 0 ? "Section" : "Hero"),
            Ui.Text(s.Context, "Body", "Ink2"),
        },
    };

    internal static View RunningCards(IReadOnlyList<RunningItem> items, bool night, SectionHost host)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, items.Count == 0 ? 0 : 12, 24, 0) };
        foreach (var item in items)
        {
            var look = EventAppearance.For(item.KindCode);
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, look.Mark + "Brush");
            var actions = new Grid { ColumnSpacing = 12 };
            for (var i = 0; i < item.Actions.Count; i++)
            {
                actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var a = item.Actions[i];
                var button = new Button { Text = a.Label, Command = a.Command };
                button.SetDynamicResource(VisualElement.StyleProperty, (night, a.IsPrimary) switch
                {
                    (true, true) => "NightPrimaryButton",
                    (true, false) => "NightGhostButton",
                    (false, true) => "PrimaryButton",
                    _ => "GhostButton",
                });
                actions.Add(button, i);
            }

            var body = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new HorizontalStackLayout { Spacing = 8, Children = { dot, Bound(item, nameof(RunningItem.Title), "Label", upper: true) } },
                    Bound(item, nameof(RunningItem.Digits), night ? "Timer" : "Window"),
                    Bound(item, nameof(RunningItem.Started), "Meta"),
                    actions,
                },
            };
            // A tap on the card opens the entry, so a timer started late can have its start moved back (Spec 007 §2).
            View card = night ? body : new Card(body);
            var id = item.Id;
            card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => host.OpenEntry(id)) });
            stack.Add(card);
        }

        return stack;
    }

    /// <summary>"2 need attention": a quiet line with a dot, only when there is something (Spec 007 §6).</summary>
    private static View Attention(AttentionSection s, SectionHost host)
    {
        if (s.Count == 0)
            return new ContentView();
        var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
        dot.SetDynamicResource(Shape.FillProperty, "AccentBrush");
        var row = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(24, 12, 24, 0), Children = { dot, Ui.Text(s.Text, "Body", "Ink2") } };
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.OpenAttention) });
        return row;
    }

    private static View Tiles(LogTilesSection s, SectionHost host)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(24, 16, 24, 0) };
        for (var i = 0; i < s.Tiles.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var t = s.Tiles[i];
            grid.Add(new LogTile(t.KindCode, t.Name, t.LastAt, t.Command, fill: true), i);
        }

        // "Earlier…" (Spec 007 §4): log something that already happened, of any kind.
        var earlier = Ui.Text(Strings.Get("Edit.Earlier"), "Label", "Ink2");
        earlier.Padding = new Thickness(0, 12);
        earlier.HorizontalOptions = LayoutOptions.End;
        earlier.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.OpenEarlier) });
        return new VerticalStackLayout { Children = { grid, new ContentView { Padding = new Thickness(24, 0), Content = earlier } } };
    }

    private static View Timeline(TimelineSection s, SectionHost host)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(24, 24, 24, 24), Spacing = 4 };
        stack.Add(Ui.Text(Strings.Get("Section.Today"), "Section"));
        if (s.Items.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2").Tap(l => l.Margin = new Thickness(0, 8)));
        foreach (var row in s.Items)
        {
            var id = row.Id;
            stack.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who, () => host.OpenEntry(id)));
        }

        return stack;
    }

    private static View Night(NightSection s, SectionHost host)
    {
        var title = Bound(s, nameof(NightSection.Title), "Meta");
        AttachLongPress(title, host.OpenDebug);
        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 4, Padding = new Thickness(24, 16, 24, 0) };
        top.Add(title.Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        top.Add(ThemeSwitch(host), 1);
        top.Add(Ui.Rounded(Ui.Text(s.BabyName, "BodyStrong"), "Surface", 999, new Thickness(14, 8), "Line"), 2);

        var stack = new VerticalStackLayout { Spacing = 20 };
        stack.Add(top);
        // The first timer in big night digits; any other (the sleep during a dream feed) as a compact card below.
        if (s.Timers.Count > 0)
            stack.Add(RunningCards([s.Timers[0]], night: true, host));
        if (s.Timers.Count > 1)
            stack.Add(RunningCards([.. s.Timers.Skip(1)], night: false, host));

        var facts = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 8) };
        foreach (var fact in s.Facts)
        {
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, EventAppearance.For(fact.KindCode).Mark + "Brush");
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
            row.Add(dot, 0);
            row.Add(Ui.Text(fact.Label, "Body", "Ink2"), 1);
            row.Add(Ui.Text(fact.Value, "BodyStrong"), 2);
            facts.Add(row);
        }

        stack.Add(facts);

        var quick = new Grid { ColumnSpacing = 10, RowSpacing = 10, Padding = new Thickness(24, 0, 24, 24) };
        quick.ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)];
        for (var i = 0; i < s.Quick.Count; i++)
        {
            if (i % 3 == 0)
                quick.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var q = s.Quick[i];
            var look = EventAppearance.For(q.KindCode);
            var button = new Button { Text = q.Label, Command = q.Command, CornerRadius = 20, MinimumHeightRequest = 68, FontFamily = "FigtreeSemiBold", FontSize = 16 };
            button.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);
            button.SetDynamicResource(Button.TextColorProperty, look.Ink);
            quick.Add(button, i % 3, i / 3);

            // A last button alone on its row (sleep, Spec 009 §3.8) takes the whole row: a bigger target at 3 AM.
            if (i == s.Quick.Count - 1 && i % 3 == 0)
                Grid.SetColumnSpan(button, 3);
        }

        stack.Add(quick);
        return stack;
    }

    /// <summary>A label whose text follows one property of a model, updated in place.</summary>
    private static Label Bound(object source, string property, string style, bool upper = false)
    {
        var label = Ui.Text("", style);
        label.SetBinding(Label.TextProperty, new Binding(property, source: source, converter: upper ? UpperCase.Instance : null));
        return label;
    }

    private sealed class UpperCase : IValueConverter
    {
        public static readonly UpperCase Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            (value as string)?.ToUpper(Strings.Culture);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private static void AttachLongPress(View view, Action? action)
    {
#if DEBUG_SCREEN
        if (action is null)
            return;
        // A long press on the wordmark opens the debug screen in debug builds (Spec 006 Q5). MAUI has no long-press
        // gesture, so three quick taps stand in for it.
        view.GestureRecognizers.Add(new TapGestureRecognizer { NumberOfTapsRequired = 3, Command = new Command(action) });
#endif
    }
}
