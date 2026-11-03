using System.ComponentModel;
using Coo.Domain.CareLog.Kinds.Feeding;
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
internal sealed record SectionHost(
    Action? OpenDebug,
    Action<EntryId> OpenEntry,
    Action OpenAttention,
    Action OpenEarlier,
    TodayViewModel Today,
    Action SwitchToLog,
    Action SwitchToAdvisor,
    Action OpenVoice,
    Action OpenHealth,
    Action<NextUpSection> OpenSleepWhy,
    Action OpenPremium,
    Action? OpenSleepPlan = null);

/// <summary>
/// A view that redraws when its source changes, coalesced to one redraw per change burst. Off screen it only notes the
/// change and redraws when it is back; once replaced, <see cref="Dispose"/> stops it listening. Views from earlier
/// rebuilds used to stay subscribed to the shared section models and redraw unseen (owner's finding on the Pixel 10).
/// </summary>
internal sealed class LiveView : ContentView, IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<View> _render;
    private readonly Func<string?, bool> _relevant;
    private bool _pending;
    private bool _stale;
    private bool _onScreen = true;

    public LiveView(INotifyPropertyChanged source, Func<View> render, Func<string?, bool> relevant)
    {
        (_source, _render, _relevant) = (source, render, relevant);
        Content = render();
        source.PropertyChanged += OnSourceChanged;
        Loaded += (_, _) =>
        {
            _onScreen = true;
            if (_stale)
                Queue();
        };
        Unloaded += (_, _) => _onScreen = false;
    }

    public void Dispose()
    {
        _source.PropertyChanged -= OnSourceChanged;
        Release(Content);
    }

    /// <summary>Stops every live view inside a view that is being thrown away (a header holds the Today switch).</summary>
    public static void Release(IVisualTreeElement? element)
    {
        if (element is LiveView live)
        {
            live.Dispose();
            return;
        }

        foreach (var child in element?.GetVisualChildren() ?? [])
            Release(child);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A section may change off the main thread (the paused banner follows the engine's status, Spec 011b §8.2).
        if (Dispatcher.IsDispatchRequired)
        {
            Dispatcher.Dispatch(() =>
            {
#pragma warning disable CA1031 // A dispatched hop: nothing may escape; the view keeps what it showed.
                try
                {
                    OnSourceChanged(sender, e);
                }
                catch (Exception)
                {
                }
#pragma warning restore CA1031
            });
            return;
        }

        if (!_relevant(e.PropertyName))
            return;
        if (_onScreen)
            Queue();
        else
            _stale = true;
    }

    private void Queue()
    {
        if (_pending)
            return;
        _pending = true;
        Dispatcher.Dispatch(() =>
        {
            _pending = false;
            _stale = false;
            var old = Content;
            Content = _render();
            Release(old);
        });
    }
}

internal static class SectionViews
{
    public static View Create(IHomeSection section, SectionHost host) => section switch
    {
        BabyHeaderSection s => Live(s, () => Header(s, host)),
        DueDateSection s => Live(s, () => DueDate(s)),
        StatusHeroSection s => Live(s, () => Hero(s), nameof(StatusHeroSection.Eyebrow), nameof(StatusHeroSection.Value), nameof(StatusHeroSection.Context)),
        RunningSection s => Live(s, () => RunningCards(s.Items, night: false, host)),
        AttentionSection s => Live(s, () => Attention(s, host)),
        LogTilesSection s => Live(s, () => Tiles(s, host)),
        NextUpSection s => Live(s, () => NextUp(s, host)),
        DailyProgressSection s => Live(s, () => DailyProgress(s)),
        AdvisorTipSection s => Live(s, () => AdvisorTip(s, host)),
        RecentActivitySection s => Live(s, () => RecentActivity(s, host)),
        TimelineSection s => Live(s, () => Timeline(s, host)),
        NightSection s => Live(s, () => Night(s, host), nameof(NightSection.Title)),
        PausedSection s => Live(s, () => Paused(s)),
        ViewOnlySection s => Live(s, () => ViewOnly(s)),
        _ => new ContentView(),
    };

    /// <summary>
    /// Re-renders the content when the section raises PropertyChanged, at most once per UI frame. Properties listed in
    /// <paramref name="boundInPlace"/> are bound to their labels instead, so they never rebuild the view.
    /// </summary>
    private static LiveView Live(INotifyPropertyChanged section, Func<View> render, params string[] boundInPlace) =>
        new(section, render, property => !boundInPlace.Contains(property));

    /// <summary>
    /// The Today switch (Spec 009 §3.7): a moon by day, a sun by night, 44 dp to tap. It redraws when the palette
    /// changes, because a section that shows in both palettes is not rebuilt then.
    /// </summary>
    private static LiveView ThemeSwitch(SectionHost host)
    {
        var view = new LiveView(host.Today, () =>
        {
            var night = host.Today.IsNight;
            return new Icon(night ? IconGeometry.Sun : IconGeometry.Sleep, "Ink2")
                .Tap(i => { i.HorizontalOptions = LayoutOptions.Center; i.VerticalOptions = LayoutOptions.Center; });
        }, property => property == nameof(TodayViewModel.IsNight))
        {
            WidthRequest = 44,
            HeightRequest = 44,
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(view, Strings.Get(host.Today.IsNight ? "Theme.ToDay" : "Theme.ToNight"));
        view.GestureRecognizers.Add(new TapGestureRecognizer { Command = host.Today.ToggleThemeCommand });
        return view;
    }

    private static View Header(BabyHeaderSection s, SectionHost host)
    {
        var mark = CooBrand.CreateMark(s.IsPremium, host.Today.IsNight, longPress: host.OpenDebug, fontSize: 24);
        var avatar = CooAvatar.Create(s.AvatarRef, s.Initial, 38);
        var chip = Ui.Rounded(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { avatar, Ui.Text($"{s.BabyName} · {s.Age}", "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center) },
        }, "Surface", 999, new Thickness(5, 5, 14, 5), "Line");

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

    /// <summary>Spec 011b §8.2: only while the shown family is soft-locked and the only family; empty otherwise.</summary>
    private static View Paused(PausedSection s)
    {
        if (s.Text.Length == 0)
            return new ContentView();
        return Ui.Rounded(Ui.Text(s.Text, "Body"), "Surface", 12, new Thickness(16, 12), "Line").Tap(v => v.Margin = new Thickness(24, 8, 24, 0));
    }

    /// <summary>
    /// "Awake for 1 h 05 m" in the big hero type; "No sleep logged yet" (no eyebrow) as a section title. The style follows the
    /// eyebrow as it changes: chosen once, a family switch to one without sleeps showed that line in hero type, filling
    /// half the screen (owner's phone, 2026-11-03).
    /// </summary>
    private static View Hero(StatusHeroSection s)
    {
        var value = Bound(s, nameof(StatusHeroSection.Value), s.Eyebrow.Length == 0 ? "Section" : "Hero");
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusHeroSection.Eyebrow))
                value.SetDynamicResource(VisualElement.StyleProperty, s.Eyebrow.Length == 0 ? "Section" : "Hero");
        }

        // The section outlives its views: the handler goes with the label.
        s.PropertyChanged += OnChanged;
        value.Unloaded += (_, _) => s.PropertyChanged -= OnChanged;
        return new VerticalStackLayout
        {
            Padding = new Thickness(24, 20, 24, 4),
            Spacing = 6,
            Children =
            {
                Bound(s, nameof(StatusHeroSection.Eyebrow), "Label", upper: true),
                value,
                Bound(s, nameof(StatusHeroSection.Context), "Body"),
            },
        };
    }

    internal static View RunningCards(IReadOnlyList<RunningItem> items, bool night, SectionHost host)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, items.Count == 0 ? 0 : 12, 24, 0) };
        foreach (var item in items)
        {
            var look = EventAppearance.For(item.KindCode);
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, look.Mark + "Brush");
            var actions = new Grid { ColumnSpacing = 12 };
            void RebuildActions()
            {
                actions.Children.Clear();
                actions.ColumnDefinitions.Clear();
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

                    // Three to a row leaves about 100 dp each: the pill's padding left "Switch & resume" no room (owner's
                    // finding). Less padding, and a long label wraps instead of being cut.
                    if (item.Actions.Count > 2)
                    {
                        button.Padding = new Thickness(8, 6);
                        button.FontSize = night ? 15 : 14;
                    }

                    actions.Add(button, i);
                }
            }

            RebuildActions();
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RunningItem.Actions))
                    RebuildActions();
            };

            View? sideIndicator = null;
            if (item.IsBreastFeed)
            {
                var leftDot = new Ellipse { WidthRequest = 6, HeightRequest = 6, VerticalOptions = LayoutOptions.Center };
                leftDot.SetDynamicResource(Shape.FillProperty, "FeedMarkBrush");
                var leftText = Ui.Text($"{Strings.Get("Feed.Left")} {item.LeftDuration}", "BodyStrong");
                var leftContent = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, Children = { leftDot, leftText } };
                var leftBorder = new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 14 },
                    Padding = new Thickness(12, 6),
                    Content = leftContent,
                };

                var rightDot = new Ellipse { WidthRequest = 6, HeightRequest = 6, VerticalOptions = LayoutOptions.Center };
                rightDot.SetDynamicResource(Shape.FillProperty, "FeedMarkBrush");
                var rightText = Ui.Text($"{Strings.Get("Feed.Right")} {item.RightDuration}", "BodyStrong");
                var rightContent = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, Children = { rightDot, rightText } };
                var rightBorder = new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 14 },
                    Padding = new Thickness(12, 6),
                    Content = rightContent,
                };

                void UpdateSidePills()
                {
                    leftText.Text = $"{Strings.Get("Feed.Left")} {item.LeftDuration}";
                    rightText.Text = $"{Strings.Get("Feed.Right")} {item.RightDuration}";

                    var isLeftActive = item.ActiveSide == BreastSide.Left;
                    var isRightActive = item.ActiveSide == BreastSide.Right;

                    leftDot.IsVisible = isLeftActive;
                    rightDot.IsVisible = isRightActive;

                    if (isLeftActive)
                    {
                        leftBorder.SetDynamicResource(VisualElement.BackgroundColorProperty, "FeedTint");
                        leftBorder.SetDynamicResource(Border.StrokeProperty, "FeedMarkBrush");
                        leftBorder.StrokeThickness = 1.5;
                        leftText.SetDynamicResource(Label.TextColorProperty, "FeedInk");
                        leftText.FontFamily = "FigtreeSemiBold";
                    }
                    else
                    {
                        leftBorder.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
                        leftBorder.SetDynamicResource(Border.StrokeProperty, "LineBrush");
                        leftBorder.StrokeThickness = 1;
                        leftText.SetDynamicResource(Label.TextColorProperty, "Ink2");
                        leftText.FontFamily = "FigtreeMedium";
                    }

                    if (isRightActive)
                    {
                        rightBorder.SetDynamicResource(VisualElement.BackgroundColorProperty, "FeedTint");
                        rightBorder.SetDynamicResource(Border.StrokeProperty, "FeedMarkBrush");
                        rightBorder.StrokeThickness = 1.5;
                        rightText.SetDynamicResource(Label.TextColorProperty, "FeedInk");
                        rightText.FontFamily = "FigtreeSemiBold";
                    }
                    else
                    {
                        rightBorder.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
                        rightBorder.SetDynamicResource(Border.StrokeProperty, "LineBrush");
                        rightBorder.StrokeThickness = 1;
                        rightText.SetDynamicResource(Label.TextColorProperty, "Ink2");
                        rightText.FontFamily = "FigtreeMedium";
                    }
                }

                UpdateSidePills();

                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(RunningItem.ActiveSide) or nameof(RunningItem.IsPaused)
                        or nameof(RunningItem.LeftDuration) or nameof(RunningItem.RightDuration))
                    {
                        UpdateSidePills();
                    }
                };

                sideIndicator = new HorizontalStackLayout { Spacing = 8, Children = { leftBorder, rightBorder }, Margin = new Thickness(0, 2, 0, 4) };
            }

            var body = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new HorizontalStackLayout { Spacing = 8, Children = { dot, Bound(item, nameof(RunningItem.Title), "Label", upper: true) } },
                    Bound(item, nameof(RunningItem.Digits), night ? "Timer" : "Window"),
                },
            };
            if (sideIndicator is not null)
                body.Add(sideIndicator);
            body.Add(Bound(item, nameof(RunningItem.Started), "Meta"));
            body.Add(actions);
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
        var earlier = new Button
        {
            Text = $"🕒 {Strings.Get("Edit.Earlier")}",
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            CornerRadius = 18,
            FontFamily = "FigtreeMedium",
            FontSize = 13,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 8, 0, 0),
        };
        earlier.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        earlier.SetDynamicResource(Button.TextColorProperty, "Ink2");
        earlier.SetDynamicResource(Button.BorderColorProperty, "Line");
        earlier.BorderWidth = 1;
        earlier.Clicked += (_, _) => host.OpenEarlier();

        // "Tell Coo" (ADR 0005): say or type it; the accent marks it as the quickest way in.
        var voice = new Border
        {
            HeightRequest = 36,
            Padding = new Thickness(14, 0),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalOptions = LayoutOptions.Start,
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Icon(IconGeometry.Mic, "OnAccent", 18) { VerticalOptions = LayoutOptions.Center },
                    Ui.Text(Strings.Get("Quick.Title"), "BodyStrong", "OnAccent").Tap(l => { l.FontSize = 13; l.VerticalOptions = LayoutOptions.Center; }),
                },
            },
        };
        voice.SetDynamicResource(VisualElement.BackgroundColorProperty, "Accent");
        voice.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.OpenVoice) });
        SemanticProperties.SetDescription(voice, Strings.Get("Quick.Title"));

        // Health and growth: medicine, temperature, vaccines, visits and measurements, and the growth chart.
        var health = new Button
        {
            Text = $"＋ {Strings.Get("Health.Short")}",
            HeightRequest = 36,
            Padding = new Thickness(14, 0),
            CornerRadius = 18,
            FontFamily = "FigtreeMedium",
            FontSize = 13,
            Margin = new Thickness(0, 8, 8, 0),
        };
        health.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        health.SetDynamicResource(Button.TextColorProperty, "Ink2");
        health.SetDynamicResource(Button.BorderColorProperty, "Line");
        health.BorderWidth = 1;
        health.Clicked += (_, _) => host.OpenHealth();

        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], Padding = new Thickness(24, 0) };
        actions.Add(voice, 0);
        actions.Add(health, 1);
        actions.Add(earlier, 2);
        return new VerticalStackLayout { Children = { grid, actions } };
    }

    private static View NextUp(NextUpSection s, SectionHost host)
    {
        if (!s.HasContent)
            return new ContentView();

        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Add(Ui.Text(Strings.Get("Today.NextUp").ToUpper(Strings.Culture), "Label", "Ink2"));

        var row = new HorizontalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center };

        if (!string.IsNullOrEmpty(s.NextSide))
        {
            var sidePill = Ui.Rounded(
                Ui.Text(s.NextSide, "BodyStrong", "FeedInk"),
                "FeedTint",
                999,
                new Thickness(12, 6));
            row.Add(sidePill);
        }

        if (!string.IsNullOrEmpty(s.SinceFeed))
        {
            var elapsedLabel = Ui.Text(s.SinceFeed, "Body", "Ink2");
            elapsedLabel.VerticalOptions = LayoutOptions.Center;
            row.Add(elapsedLabel);
        }

        if (row.Children.Count > 0)
            stack.Add(row);

        if (!string.IsNullOrEmpty(s.NextNap))
        {
            // The next sleep (ADR 0008 §7): its window, and one tap to why (Premium) or to Premium.
            var napRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 8 };
            var napIcon = new Icon(IconGeometry.Sleep, "SleepInk", 18) { VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) };
            var napText = new VerticalStackLayout { Spacing = 2 };
            napText.Add(Ui.Text(s.NextNap, s.NapIsPersonal ? "BodyStrong" : "Body", s.NapIsPersonal ? "Ink" : "Ink2"));
            if (!string.IsNullOrEmpty(s.NapWindow))
                napText.Add(Ui.Text(s.NapWindow, "Meta", s.NapIsPersonal || s.NapUpsell ? "Accent" : "Ink2"));
            napRow.Add(napIcon, 0);
            napRow.Add(napText, 1);
            if (s.NapIsPersonal)
                napRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => host.OpenSleepWhy(s)) });
            else if (s.NapUpsell)
                napRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.OpenPremium) });
            stack.Add(napRow);
        }

        if (!string.IsNullOrEmpty(s.PlanLine))
        {
            var planRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
            planRow.Add(new Icon(IconGeometry.Sleep, "SleepInk", 18) { VerticalOptions = LayoutOptions.Center }, 0);
            planRow.Add(Ui.Text(s.PlanLine, "Body", "Accent").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
            planRow.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 16) { VerticalOptions = LayoutOptions.Center }, 2);
            if (host.OpenSleepPlan is { } open)
                planRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(open) });
            stack.Add(planRow);
        }

        var card = new Card(stack);
        card.Margin = new Thickness(24, 12, 24, 0);
        return card;
    }

    private static View ViewOnly(ViewOnlySection s)
    {
        if (string.IsNullOrEmpty(s.Text))
            return new ContentView();
        var note = Ui.Rounded(Ui.Text(s.Text, "Body", "Ink2"), "Surface", 16, new Thickness(16, 12), "Line");
        note.Margin = new Thickness(24, 12, 24, 0);
        return note;
    }

    private static View DailyProgress(DailyProgressSection s)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(Ui.Text(Strings.Get("Today.DailyProgress").ToUpper(Strings.Culture), "Label", "Ink2"));

        View MetricRow(string iconGeo, string kindCode, string text, double ratio)
        {
            var look = EventAppearance.For(kindCode);
            var icon = new Icon(iconGeo, look.Ink, 18) { VerticalOptions = LayoutOptions.Center };
            var label = Ui.Text(text, "BodyStrong", "Ink");
            label.VerticalOptions = LayoutOptions.Center;

            // Mini progress indicator bar
            var barTrack = new Border
            {
                HeightRequest = 6,
                StrokeShape = new RoundRectangle { CornerRadius = 3 },
                StrokeThickness = 0,
                Padding = 0,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
            };
            barTrack.SetDynamicResource(VisualElement.BackgroundColorProperty, "Line");

            var clampedRatio = Math.Clamp(ratio, 0.0, 1.0);
            var barFill = new Border
            {
                HeightRequest = 6,
                StrokeShape = new RoundRectangle { CornerRadius = 3 },
                StrokeThickness = 0,
                Padding = 0,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
            };
            barFill.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Mark);

            var fillWeight = clampedRatio > 0.001 ? Math.Max(0.04, clampedRatio) : 0.0001;
            var remainingWeight = Math.Max(0.0001, 1.0 - clampedRatio);

            var barGrid = new Grid
            {
                HeightRequest = 6,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
                ColumnDefinitions =
                [
                    new ColumnDefinition(new GridLength(fillWeight, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(remainingWeight, GridUnitType.Star))
                ],
            };
            barGrid.Add(barTrack, 0, 0);
            Grid.SetColumnSpan(barTrack, 2);
            if (clampedRatio > 0.01)
            {
                barGrid.Add(barFill, 0, 0);
            }

            var rowGrid = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = 8,
                VerticalOptions = LayoutOptions.Center,
            };
            rowGrid.Add(icon, 0);
            rowGrid.Add(label, 1);

            var pct = Ui.Text($"{Math.Round(clampedRatio * 100)}%", "Meta", "Ink2");
            pct.VerticalOptions = LayoutOptions.Center;
            rowGrid.Add(pct, 2);

            return new VerticalStackLayout
            {
                Spacing = 4,
                Children = { rowGrid, barGrid }
            };
        }

        stack.Add(MetricRow(IconGeometry.Feed, "feeding", s.FeedsProgress, s.FeedsRatio));
        stack.Add(MetricRow(IconGeometry.Diaper, "diaper", s.DiapersProgress, s.DiapersRatio));
        stack.Add(MetricRow(IconGeometry.Sleep, "sleep", s.SleepProgress, s.SleepRatio));

        var card = new Card(stack);
        card.Margin = new Thickness(24, 12, 24, 0);
        return card;
    }

    private static View AdvisorTip(AdvisorTipSection s, SectionHost host)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };

        var topRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8,
            VerticalOptions = LayoutOptions.Center
        };

        var headerStack = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
        headerStack.Add(Ui.Text("💡", "Body"));
        headerStack.Add(Ui.Text(Strings.Get("Today.AdvisorTip").ToUpper(Strings.Culture), "Label", "Ink2"));

        var arrow = new Icon(IconGeometry.ChevronRight, "Ink2", 18) { VerticalOptions = LayoutOptions.Center };

        topRow.Add(headerStack, 0);
        topRow.Add(arrow, 1);

        stack.Add(topRow);
        stack.Add(Ui.Text(s.StageBadge, "Meta", "SleepInk"));
        stack.Add(Ui.Text(s.TipText, "Body", "Ink"));

        var card = new Card(stack);
        card.Margin = new Thickness(24, 12, 24, 0);
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.SwitchToAdvisor) });
        return card;
    }

    private static View RecentActivity(RecentActivitySection s, SectionHost host)
    {
        var stack = new VerticalStackLayout { Spacing = 4, Margin = new Thickness(24, 16, 24, 16) };
        stack.Add(Ui.Text(Strings.Get("Today.RecentActivity"), "Section"));

        if (s.Items.Count == 0)
        {
            stack.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2").Tap(l => l.Margin = new Thickness(0, 8)));
        }
        else
        {
            foreach (var item in s.Items)
            {
                if (item.Id is { } id)
                    stack.Add(new TimelineRow(item.Time, item.KindCode, item.Title, item.Who, () => host.OpenEntry(id)));
            }

            var link = Ui.Text(Strings.Get("Today.SeeAllInLog"), "BodyStrong", "AccentInk");
            link.Padding = new Thickness(0, 8, 0, 4);
            link.HorizontalOptions = LayoutOptions.Start;
            link.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.SwitchToLog) });
            stack.Add(link);
        }

        return stack;
    }

    private static View Timeline(TimelineSection s, SectionHost host)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(24, 24, 24, 24), Spacing = 4 };
        stack.Add(Ui.Text(Strings.Get("Section.Today"), "Section"));
        if (s.Items.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2").Tap(l => l.Margin = new Thickness(0, 8)));
        foreach (var row in s.Items)
        {
            if (row.Id is { } id)
                stack.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who, () => host.OpenEntry(id)));
        }

        return stack;
    }

    private static View Night(NightSection s, SectionHost host)
    {
        var mark = CooBrand.CreateMark(s.IsPremium, host.Today.IsNight, longPress: host.OpenDebug, fontSize: 24);
        var avatar = CooAvatar.Create(s.AvatarRef, s.Initial, 38);

        var chip = Ui.Rounded(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { avatar, Ui.Text($"{s.BabyName} · {s.Age}", "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center) },
        }, "Surface", 999, new Thickness(5, 5, 14, 5), "Line");

        var top = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 4, Padding = new Thickness(24, 16, 24, 8) };
        top.Add(mark, 0);
        top.Add(ThemeSwitch(host), 1);
        top.Add(chip, 2);

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
        // Three quick taps open the debug screen in debug builds (Spec 006 Q5), counted from single taps: on Android MAUI
        // never reports a third tap to NumberOfTapsRequired = 3 (Spec 010a §5, the Pixel 10).
        var burst = new TapBurst(TimeProvider.System);
        view.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                if (burst.Tap())
                    action();
            }),
        });
#endif
    }
}
