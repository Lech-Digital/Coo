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
    Action? OpenSleepPlan = null,
    Action? ChooseBaby = null,
    Action? OpenMilkStash = null,
    Action<string>? ShareText = null);

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
        SiblingsSection s => Live(s, () => Siblings(s, host)),
        HandoverSection s => Live(s, () => Handover(s, host)),
        LastNightSection s => Live(s, () => LastNight(s)),
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

    /// <summary>
    /// The top bar (<see cref="PageFrame.Bar"/>) and the page title: "Today", with "Lena · 7 weeks · Thursday 5 November"
    /// under it and her picture beside it when she has one. With twins or siblings the title chooses the baby.
    /// </summary>
    private static View Header(BabyHeaderSection s, SectionHost host)
    {
        var bar = PageFrame.Bar(CooBrand.CreateMark(s.IsPremium, host.Today.IsNight, longPress: host.OpenDebug, fontSize: PageFrame.WordmarkSize), ThemeSwitch(host));
        var line = s.BabyName.Length == 0 ? s.Date : $"{s.BabyName} · {s.Age} · {s.Date}{(s.HasSiblings ? " ▾" : "")}";
        var avatar = s.AvatarRef is { Length: > 0 } avatarRef ? CooAvatar.Create(avatarRef, s.Initial, 48) : null;
        var title = PageFrame.Title(Strings.Get("Section.Today"), line, avatar);
        title.Margin = new Thickness(PageFrame.Side, 0, PageFrame.Side, 4);
        if (s.HasSiblings && host.ChooseBaby is { } choose)
            title.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(choose) });
        return new VerticalStackLayout { Children = { bar, title } };
    }

    private static View LastNight(LastNightSection s)
    {
        if (s.First.Length == 0)
            return new ContentView();
        var look = EventAppearance.For("sleep");
        var stack = new VerticalStackLayout { Spacing = 4 };
        var title = new HorizontalStackLayout { Spacing = 8 };
        title.Add(new Icon(IconGeometry.Sleep, look.Ink, 18) { VerticalOptions = LayoutOptions.Center });
        title.Add(Ui.Text(Strings.Get("Night.Title"), "BodyStrong", look.Ink).Tap(l => l.VerticalOptions = LayoutOptions.Center));
        stack.Add(title);
        stack.Add(Ui.Text(s.First, "Body"));
        if (s.Second.Length > 0)
            stack.Add(Ui.Text(s.Second, "Meta", "Ink2"));
        return Ui.Rounded(stack, look.Tint, 18, new Thickness(16, 12)).Tap(v => v.Margin = new Thickness(24, 12, 24, 0));
    }

    /// <summary>
    /// "While you were away" and the nanny's handover: who looked after the baby and when, what happened as chips, and
    /// Got it / Seen (a parent) or Share (the caregiver). The away card is outlined in the accent (owner, 2026-11-05).
    /// </summary>
    private static View Handover(HandoverSection s, SectionHost host)
    {
        if (s.Cards.Count == 0)
            return new ContentView();
        var stack = new VerticalStackLayout { Spacing = 10, Margin = new Thickness(PageFrame.Side, 8, PageFrame.Side, 0) };
        foreach (var card in s.Cards)
        {
            var body = new VerticalStackLayout { Spacing = 10 };
            var head = new VerticalStackLayout { Spacing = 1 };
            head.Add(Ui.Text(card.Title, "BodyStrong").Tap(l => l.FontSize = 16));
            head.Add(Ui.Text(card.Span, "Meta", "Ink2"));
            body.Add(head);
            var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var line in card.Lines)
                chips.Add(Ui.Rounded(Ui.Text(line, "Meta", "AccentInk").Tap(l => l.FontFamily = "FigtreeSemiBold"), "AccentSoft", 15, new Thickness(10, 6)).Tap(c => c.Margin = new Thickness(0, 0, 6, 6)));
            body.Add(chips);
            var (person, last, text) = (card.Person, card.Last, card.ShareText);
            var label = Strings.Get(card.IsMine ? "Handover.Share" : card.IsAway ? "Away.GotIt" : "Handover.Seen");
            Func<Task> act = card.IsMine
                ? () => { host.ShareText?.Invoke(text); return Task.CompletedTask; }
                : card.IsAway ? host.Today.DismissAwayAsync : () => host.Today.MarkHandoverSeenAsync(person, last);
            var button = card.IsAway ? PageFrame.Primary(label, act) : PageFrame.Secondary(label, act);
            body.Add(button);
            var frame = Ui.Rounded(body, "Glass", 22, new Thickness(16), card.IsAway ? "Accent" : "GlassLine");
            if (card.IsAway)
                frame.StrokeThickness = 1.5;
            stack.Add(frame);
        }

        return stack;
    }

    private static View Siblings(SiblingsSection s, SectionHost host)
    {
        if (s.Siblings.Count == 0)
            return new ContentView();
        var stack = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(24, 4, 24, 0) };
        foreach (var sibling in s.Siblings)
        {
            var row = Ui.Rounded(new HorizontalStackLayout
            {
                Spacing = 6,
                Children = { Ui.Text(sibling.Name, "BodyStrong"), Ui.Text("· " + sibling.Line, "Body", "Ink2") },
            }, "Surface", 14, new Thickness(14, 10), "Line");
            var id = sibling.Id;
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await host.Today.FocusAsync(id)) });
            SemanticProperties.SetDescription(row, $"{Strings.Get("Sibling.Choose")} {sibling.Name}: {sibling.Line}");
            stack.Add(row);
        }

        // "Log for both": diapers, bottles and sleeps go to each of them, until turned off.
        if (s.LogForAllLabel.Length > 0)
        {
            var toggle = new Button { Text = (s.LogForAll ? "✓ " : "") + s.LogForAllLabel, HorizontalOptions = LayoutOptions.Start };
            toggle.SetDynamicResource(VisualElement.StyleProperty, s.LogForAll ? "PrimaryButton" : "GhostButton");
            toggle.Clicked += async (_, _) => await host.Today.ToggleLogForAllAsync();
            stack.Add(toggle);
        }

        return stack;
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
    /// The status card: Mochi (ears up awake, ears down asleep), "AWAKE FOR", the time and since when. While a sleep runs
    /// its timer card is the status card instead (one card, not two: owner, 2026-11-05). "No sleep logged yet" is a
    /// plain line. Eyebrow, value and context are bound in place, so the clock ticking never rebuilds it.
    /// </summary>
    private static View Hero(StatusHeroSection s)
    {
        if (s.ShownByTimer)
            return new ContentView();
        var value = Bound(s, nameof(StatusHeroSection.Value), s.Eyebrow.Length == 0 ? "Section" : "Hero");
        if (s.Eyebrow.Length > 0)
            value.FontSize = 42;
        if (s.Mood == MochiMood.None)
            return value.Tap(v => v.Margin = new Thickness(PageFrame.Side, 12, PageFrame.Side, 0));

        var mochi = new Image
        {
            Source = s.Mood == MochiMood.Asleep ? "mochi_asleep.png" : "mochi_awake.png",
            WidthRequest = 56,
            HeightRequest = 56,
            VerticalOptions = LayoutOptions.Center,
            Aspect = Aspect.AspectFit,
        };
        AutomationProperties.SetIsInAccessibleTree(mochi, false); // decoration: the eyebrow already says awake or asleep
        var text = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                Bound(s, nameof(StatusHeroSection.Eyebrow), "Label", upper: true),
                value,
                Bound(s, nameof(StatusHeroSection.Context), "Meta"),
            },
        };
        var grid = new Grid { ColumnSpacing = 14, ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        grid.Add(mochi, 0);
        grid.Add(text, 1);
        return new Card(grid).Tap(c => c.Margin = new Thickness(PageFrame.Side, 12, PageFrame.Side, 0));
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
                    // At night the digits share the card with Mochi: 54 sp, not the full-width timer size.
                    Bound(item, nameof(RunningItem.Digits), night ? "Timer" : "Window").Tap(l =>
                    {
                        if (night)
                            l.FontSize = 54;
                        l.MaxLines = 1;
                        l.LineBreakMode = LineBreakMode.NoWrap;
                    }),
                },
            };
            if (sideIndicator is not null)
                body.Add(sideIndicator);
            body.Add(Bound(item, nameof(RunningItem.Started), "Meta"));
            body.Add(actions);
            // A running sleep is the status card (owner, 2026-11-05): the sleeping Mochi beside its timer.
            View content = body;
            if (item.KindCode == "sleep")
            {
                var mochi = new Image { Source = "mochi_asleep.png", WidthRequest = night ? 52 : 56, HeightRequest = night ? 52 : 56, VerticalOptions = LayoutOptions.Start, Aspect = Aspect.AspectFit };
                AutomationProperties.SetIsInAccessibleTree(mochi, false);
                body.Remove(actions);
                var withMochi = new Grid
                {
                    ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                    RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                    ColumnSpacing = 14,
                    RowSpacing = 12,
                };
                withMochi.Add(mochi, 0, 0);
                withMochi.Add(body, 1, 0);
                withMochi.Add(actions, 0, 1);
                Grid.SetColumnSpan(actions, 2);
                content = withMochi;
            }

            // A tap on the card opens the entry, so a timer started late can have its start moved back (Spec 007 §2).
            View card = new Card(content);
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

    /// <summary>
    /// The four shortcuts (each logs or starts its kind; four in a row, two by two when narrow), then two equal buttons:
    /// Tell Mochi, and Log something, which holds everything else (owner, 2026-11-05: Health and Earlier… are gone).
    /// </summary>
    private static View Tiles(LogTilesSection s, SectionHost host)
    {
        var grid = AdaptiveColumns.Create(
            [.. s.Tiles.Select(t => (View)new LogTile(t.KindCode, t.Name, t.LastAt, t.Command, fill: true))],
            minWidth: 70, padding: new Thickness(PageFrame.Side, 12, PageFrame.Side, 0));

        View Pill(string icon, string text, string background, string ink, string? line, Action tap)
        {
            var row = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            row.Add(new Icon(icon, ink, 18) { VerticalOptions = LayoutOptions.Center });
            row.Add(Ui.Text(text, "BodyStrong", ink).Tap(l => { l.FontSize = 15; l.VerticalOptions = LayoutOptions.Center; l.LineBreakMode = LineBreakMode.TailTruncation; }));
            var pill = Ui.Rounded(row, background, 24, new Thickness(12, 0), line);
            pill.HeightRequest = 48;
            pill.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(tap) });
            SemanticProperties.SetDescription(pill, text);
            return pill;
        }

        // Side by side when both words fit half the width; one under the other with large text or a long language
        // ("Log something" was cut at 1.3×, 2026-11-05).
        var (voice, more) = (Strings.Get("Quick.Title"), Strings.Get("Today.LogSomething"));
        var display = DeviceDisplay.MainDisplayInfo;
        var half = (display.Width / Math.Max(1, display.Density) - 2 * PageFrame.Side - 8) / 2;
        static double Need(string text) => 18 + 8 + 24 + (text.Length * 15 * 0.56 * TextScale.Factor);
        var stacked = Need(voice) > half || Need(more) > half;
        var actions = new Grid
        {
            ColumnDefinitions = stacked ? [new(GridLength.Star)] : [new(GridLength.Star), new(GridLength.Star)],
            RowDefinitions = stacked ? [new(GridLength.Auto), new(GridLength.Auto)] : [new(GridLength.Auto)],
            ColumnSpacing = 8,
            RowSpacing = 8,
            Padding = new Thickness(PageFrame.Side, 10, PageFrame.Side, 0),
        };
        actions.Add(Pill(IconGeometry.Mic, voice, "VoiceBg", "VoiceInk", null, host.OpenVoice), 0, 0);
        actions.Add(Pill(IconGeometry.Plus, more, "Glass", "Ink", "GlassLine", host.OpenEarlier), stacked ? 0 : 1, stacked ? 1 : 0);
        return new VerticalStackLayout { Children = { grid, actions } };
    }

    /// <summary>
    /// "NEXT UP": the side to offer and the time since the last feed, the next nap (its why, or Premium, one tap away),
    /// the sleep plan's day and the milk stash, one row each with its kind's icon.
    /// </summary>
    private static View NextUp(NextUpSection s, SectionHost host)
    {
        if (!s.HasContent)
            return new ContentView();

        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(PageFrame.SectionLabel(Strings.Get("Today.NextUp")));

        View Row(string icon, string iconInk, string text, string? value, string textKey = "Ink", Action? tap = null)
        {
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
            row.Add(new Icon(icon, iconInk, 18) { VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(Ui.Text(text, "Body", textKey).Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
            if (!string.IsNullOrEmpty(value))
                row.Add(Ui.Text(value, "BodyStrong").Tap(l => { l.VerticalOptions = LayoutOptions.Center; l.HorizontalTextAlignment = TextAlignment.End; }), 2);
            else if (tap is not null)
                row.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 16) { VerticalOptions = LayoutOptions.Center }, 2);
            if (tap is not null)
                row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(tap) });
            return row;
        }

        if (!string.IsNullOrEmpty(s.NextSide) || !string.IsNullOrEmpty(s.SinceFeed))
            stack.Add(Row(IconGeometry.Feed, "FeedInk", s.NextSide.Length > 0 ? s.NextSide : s.SinceFeed, s.NextSide.Length > 0 ? s.SinceFeed : null));

        if (!string.IsNullOrEmpty(s.NextNap))
        {
            // The next sleep (ADR 0008 §7): its window, and one tap to why (Premium) or to Premium.
            var nap = new VerticalStackLayout { Spacing = 1 };
            nap.Add(Ui.Text(s.NextNap, s.NapIsPersonal ? "BodyStrong" : "Body", s.NapIsPersonal ? "Ink" : "Ink2"));
            if (!string.IsNullOrEmpty(s.NapWindow))
                nap.Add(Ui.Text(s.NapWindow, "Meta", s.NapIsPersonal || s.NapUpsell ? "AccentInk" : "Ink2"));
            var napRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10 };
            napRow.Add(new Icon(IconGeometry.Sleep, "SleepInk", 18) { VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) }, 0);
            napRow.Add(nap, 1);
            if (s.NapIsPersonal)
                napRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => host.OpenSleepWhy(s)) });
            else if (s.NapUpsell)
                napRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(host.OpenPremium) });
            stack.Add(napRow);
        }

        if (!string.IsNullOrEmpty(s.PlanLine))
            stack.Add(Row(IconGeometry.Sleep, "SleepInk", s.PlanLine, null, "AccentInk", host.OpenSleepPlan));
        if (!string.IsNullOrEmpty(s.StashLine))
            stack.Add(Row(IconGeometry.Pump, "PumpInk", s.StashLine, null, "AccentInk", host.OpenMilkStash));

        return new Card(stack).Tap(c => c.Margin = new Thickness(PageFrame.Side, 12, PageFrame.Side, 0));
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
        var top = PageFrame.Bar(CooBrand.CreateMark(s.IsPremium, host.Today.IsNight, longPress: host.OpenDebug, fontSize: PageFrame.WordmarkSize), ThemeSwitch(host));

        var stack = new VerticalStackLayout { Spacing = 16 };
        stack.Add(top);
        // The first timer in big night digits; any other (the sleep during a dream feed) as a compact card below.
        if (s.Timers.Count > 0)
            stack.Add(RunningCards([s.Timers[0]], night: true, host));
        if (s.Timers.Count > 1)
            stack.Add(RunningCards([.. s.Timers.Skip(1)], night: false, host));

        // The last of each, as a grouped list on glass.
        var factRows = new List<View>();
        foreach (var fact in s.Facts)
        {
            var dot = new Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Shape.FillProperty, EventAppearance.For(fact.KindCode).Mark + "Brush");
            // The label keeps its line and the value wraps on the right: "Last feed" broke over two lines beside a
            // long "9:52 AM · left 8 m · right 13 m" with large text (2026-11-05).
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10, Padding = new Thickness(16, 12) };
            row.Add(dot, 0);
            row.Add(Ui.Text(fact.Label, "Body", "Ink2").Tap(l => l.LineBreakMode = LineBreakMode.NoWrap), 1);
            row.Add(Ui.Text(fact.Value, "BodyStrong").Tap(l => l.HorizontalTextAlignment = TextAlignment.End), 2);
            factRows.Add(row);
        }

        if (factRows.Count > 0)
            stack.Add(new ContentView { Padding = new Thickness(PageFrame.Side, 0), Content = PageFrame.Group(factRows) });

        var quick = new Grid { ColumnSpacing = 10, RowSpacing = 10, Padding = new Thickness(24, 0, 24, 24) };
        quick.ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)];
        // "Woke up" is already on the sleep's own card: no second one under the quick buttons.
        var sleeping = s.Timers.Any(t => t.KindCode == "sleep");
        var buttons = s.Quick.Where(q => !(sleeping && q.KindCode == "sleep")).ToList();
        for (var i = 0; i < buttons.Count; i++)
        {
            if (i % 3 == 0)
                quick.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var q = buttons[i];
            var look = EventAppearance.For(q.KindCode);
            var button = new Button { Text = q.Label, Command = q.Command, CornerRadius = 20, MinimumHeightRequest = 68, FontFamily = "FigtreeSemiBold", FontSize = 16 };
            button.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);
            button.SetDynamicResource(Button.TextColorProperty, look.Ink);
            quick.Add(button, i % 3, i / 3);

            // A last button alone on its row (sleep, Spec 009 §3.8) takes the whole row: a bigger target at 3 AM.
            if (i == buttons.Count - 1 && i % 3 == 0)
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
