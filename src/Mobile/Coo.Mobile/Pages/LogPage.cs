using System.ComponentModel;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Log (Spec 006 §7): one day at a time, with the ribbon, totals and the timeline.</summary>
public sealed class LogPage : ContentPage
{
    private readonly LogViewModel _model;
    private readonly ThemeController _theme;
    private readonly IServiceProvider _services;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private readonly LanguageController _language;
    private bool _renderQueued;
    private string? _renderedLanguage;
    private RibbonView? _ribbon;
    private readonly CooPageHeader _pageHeader;
    private readonly Sheet _sheet = new();
    private readonly DayDetailView _detail = new();
    private readonly ContentView _totalsContainer = new() { Padding = new Thickness(24, 0, 24, 0) };
    private readonly VerticalStackLayout _logContentContainer = new() { Padding = new Thickness(24, 0, 24, 0), Spacing = 16 };

    public LogPage(LogViewModel model, ThemeController theme, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;
        _language = language;
        _undo = undo;
        _theme = theme;
        _services = services;
        _pageHeader = new CooPageHeader(services);
        Shell.SetNavBarIsVisible(this, false);

        var outerStack = new VerticalStackLayout
        {
            Padding = new Thickness(0, 0, 0, 24),
            Spacing = 16,
            Children = { _pageHeader, _totalsContainer, _logContentContainer }
        };
        _body.Content = outerStack;

        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        root.Add(_sheet);

        // The page owns this sheet and closes it itself; closing never goes through a view model (Spec 009b §3).
        _sheet.Closed += async (_, _) => await _sheet.HideAsync();
        _detail.EntryTapped += async (_, id) =>
        {
            await _sheet.HideAsync();
            OpenEntry(id);
        };
        Content = root;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _undo.DataChanged -= OnDataChanged;
        _model.PropertyChanged -= OnChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
        _language.Changed -= OnLanguageChanged;
        var billing = _services.GetService<Coo.Mobile.Application.Billing.IBillingService>();
        if (billing != null) billing.EntitlementChanged -= QueueRender;
    }

    // After an undo, or a delete that the window commits, the page shows what is now stored.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await _model.RefreshAsync());

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var billing = _services.GetService<Coo.Mobile.Application.Billing.IBillingService>();
        if (billing != null) billing.EntitlementChanged += QueueRender;
        // Listening only while on screen: the view model outlives this page (owner's finding on the Pixel 10).
        _undo.DataChanged += OnDataChanged;
        _model.PropertyChanged += OnChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        _language.Changed += OnLanguageChanged;

        // The first visit draws at once from the data loaded at start-up, before the page's first frame: drawing after
        // the refresh showed one blank frame (recorded on the Pixel 10). A refresh with nothing new raises no change,
        // and then nothing is redrawn.
        // The band's colours are drawn, not resources: a palette change while Log was off screen (the moon on Today) left
        // it in the old colours (found on the Pixel 10). Redrawing the band alone is cheap.
        if (_logContentContainer.Children.Count == 0)
            Render();
        else
            ShowRibbon();
        _pageHeader.Refresh();
        _model.ResetToToday();
        await _model.RefreshAsync();
        if (_renderedLanguage != Strings.Language)
            QueueRender();
    }

    // "Now" moves every minute: it redraws the band, not the page.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.NowAt))
            Dispatcher.Dispatch(ShowRibbon);
        else
            QueueRender();
    }

    private void ShowRibbon()
    {
        var palette = WashiPalette.For(_theme.Current);
        _ribbon?.Show(_model.ChartItems, _model.QuietBands, _model.Hours, _model.NowAt, palette);
        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, palette);
    }

    private async void OpenDetail(double at)
    {
        _detail.Show(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current), _model.RangeTitle, at);
        await _sheet.ShowAsync(_detail);
    }

    /// <summary>Back closes the zoomed day before it leaves the page.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_sheet.IsVisible)
        {
            _ = _sheet.HideAsync();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    // The ribbon is drawn with the palette's colours, not resources.
    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await _model.RefreshAsync();
        QueueRender();
    });

    /// <summary>Several changes in a row become one redraw.</summary>
    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private async void OpenEntry(Coo.Domain.Common.EntryId id)
    {
        var page = _services.GetRequiredService<EditPage>();
        await page.OpenAsync(id);
        await Navigation.PushAsync(page);
    }

    private void Render()
    {
        if (_model.IsStatsView)
        {
            RenderStatsContent();
            return;
        }

        // Chevron icons, not the ‹ › characters: Figtree has no glyphs for them (Increment 6 finding on the phone).
        View Nav(string icon, System.Windows.Input.ICommand command, bool enabled)
        {
            var circle = Ui.Rounded(new Icon(icon, enabled ? "Ink" : "Line", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
            circle.WidthRequest = 48;
            circle.HeightRequest = 48;
            if (enabled)
                circle.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
            return circle;
        }

        var titleStack = new VerticalStackLayout
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                Ui.Text(_model.Title, "Title").Tap(l =>
                {
                    l.LineBreakMode = LineBreakMode.TailTruncation;
                    l.MaxLines = 1;
                }),
                Ui.Text(_model.Subtitle, "Meta", "Ink2").Tap(l =>
                {
                    l.LineBreakMode = LineBreakMode.TailTruncation;
                    l.MaxLines = 1;
                }),
            },
        };

        var toggleBtn = new Button
        {
            Text = Strings.Get("Stats.SwitchView"),
            Command = _model.ToggleViewCommand,
            HeightRequest = 38,
            Padding = new Thickness(12, 0),
            VerticalOptions = LayoutOptions.Center,
        };
        toggleBtn.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");

        var header = new Grid
        {
            MinimumHeightRequest = 48,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8,
            VerticalOptions = LayoutOptions.Center,
        };
        header.Add(titleStack, 0);
        header.Add(toggleBtn, 1);
        header.Add(Nav(IconGeometry.ChevronLeft, _model.PreviousCommand, true), 2);
        header.Add(Nav(IconGeometry.ChevronRight, _model.NextCommand, _model.CanGoNext), 3);

        _ribbon = new RibbonView();
        ShowRibbon();
        _ribbon.Tapped += (_, at) => OpenDetail(at);
        SemanticProperties.SetDescription(_ribbon, _model.ChartSummary);
        SemanticProperties.SetHint(_ribbon, Strings.Get("Chart.Hint"));

        var legend = new HorizontalStackLayout { Spacing = 14 };
        // The legend names the kinds that have totals, so hidden kinds leave both (Spec 009 §3.2).
        var names = new Dictionary<string, string> { ["feeding"] = "Kind.Feed", ["diaper"] = "Kind.Diaper", ["sleep"] = "Kind.Sleep", ["pump"] = "Kind.Pump" };
        foreach (var (kind, key) in _model.Totals.Where(t => names.ContainsKey(t.KindCode)).Select(t => (t.KindCode, names[t.KindCode])))
        {
            var dot = new Microsoft.Maui.Controls.Shapes.Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Microsoft.Maui.Controls.Shapes.Shape.FillProperty, EventAppearance.For(kind).Mark + "Brush");
            legend.Add(new HorizontalStackLayout { Spacing = 5, Children = { dot, Ui.Text(Strings.Get(key), "Meta") } });
        }

        var totals = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < _model.Totals.Count; i++)
        {
            totals.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var t = _model.Totals[i];
            totals.Add(new TotalsCell(t.KindCode, t.Number, t.Unit), i);
        }

        var timeline = new VerticalStackLayout { Spacing = 4 };
        if (_model.Rows.Count == 0)
            timeline.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2"));
        foreach (var row in _model.Rows)
        {
            if (row.IsMidnight)
            {
                timeline.Add(new MidnightRow(row.Title));
                continue;
            }

            var id = row.Id;
            timeline.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who, () => OpenEntry(id)));
        }

        _renderedLanguage = Strings.Language;
        _totalsContainer.Content = totals;
        _logContentContainer.Children.Clear();
        _logContentContainer.Add(header);
        _logContentContainer.Add(_ribbon);
        _logContentContainer.Add(legend);
        _logContentContainer.Add(timeline);

        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current));
    }

    private void RenderStatsContent()
    {
        _totalsContainer.Content = null;

        var header = new Grid
        {
            MinimumHeightRequest = 48,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8,
            VerticalOptions = LayoutOptions.Center,
        };

        var titleStack = new VerticalStackLayout
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                Ui.Text(Strings.Get("Stats.Title"), "Title"),
                Ui.Text(_model.StatsPeriodTitle, "Meta", "Ink2"),
            },
        };

        View Nav(string icon, System.Windows.Input.ICommand command, bool enabled)
        {
            var circle = Ui.Rounded(new Icon(icon, enabled ? "Ink" : "Line", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
            circle.WidthRequest = 48;
            circle.HeightRequest = 48;
            if (enabled)
                circle.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
            return circle;
        }

        var toggleBtn = new Button
        {
            Text = Strings.Get("Stats.TimelineView"),
            Command = _model.ToggleViewCommand,
            HeightRequest = 38,
            Padding = new Thickness(12, 0),
            VerticalOptions = LayoutOptions.Center,
        };
        toggleBtn.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");

        header.Add(titleStack, 0);
        header.Add(toggleBtn, 1);
        header.Add(Nav(IconGeometry.ChevronLeft, _model.PreviousPeriodCommand, true), 2);
        header.Add(Nav(IconGeometry.ChevronRight, _model.NextPeriodCommand, _model.CanGoNextPeriod), 3);

        var periodBar = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        View PeriodChip(string label, CarePeriod period)
        {
            var isSelected = _model.StatsPeriod == period;
            var btn = new Button
            {
                Text = label,
                Command = _model.SetPeriodCommand,
                CommandParameter = period,
                HeightRequest = 42,
            };
            btn.SetDynamicResource(VisualElement.StyleProperty, isSelected ? "PrimaryButton" : "GhostButton");
            return btn;
        }

        periodBar.Add(PeriodChip(Strings.Get("Stats.Day"), CarePeriod.Day), 0);
        periodBar.Add(PeriodChip(Strings.Get("Stats.Week"), CarePeriod.Week), 1);
        periodBar.Add(PeriodChip(Strings.Get("Stats.Month"), CarePeriod.Month), 2);

        var cards = new VerticalStackLayout { Spacing = 12 };

        if (_model.Stats is { } stats)
        {
            var sleepLook = EventAppearance.For("sleep");
            var sleepCard = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Rounded(new Icon(sleepLook.Icon, sleepLook.Ink, 20), sleepLook.Tint, 12, new Thickness(6)),
                            Ui.Text(Strings.Get("Kind.Sleep"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Text(Formats.Duration(stats.Sleep.DailyAverage), "Title", sleepLook.Ink),
                            Ui.Text("/ " + Strings.Get("Stats.Day").ToLower(Strings.Culture), "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    Ui.Text($"{Strings.Get("Stats.TotalSleep")}: {Formats.Duration(stats.Sleep.TotalSleep)} · {stats.Sleep.TotalSessions} sleeps ({stats.Sleep.SessionsPerDay}/day)", "Meta", "Ink2"),
                    Ui.Text($"{Strings.Get("Stats.AverageDuration")}: {Formats.Duration(stats.Sleep.AverageDuration)} · {Strings.Get("Stats.LongestStretch")}: {Formats.Duration(stats.Sleep.LongestStretch)}", "Meta", "Ink2"),
                }
            };
            cards.Add(new Card(sleepCard));

            var feedLook = EventAppearance.For("feeding");
            var feedCard = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Rounded(new Icon(feedLook.Icon, feedLook.Ink, 20), feedLook.Tint, 12, new Thickness(6)),
                            Ui.Text(Strings.Get("Kind.Feed"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Text(stats.Feeding.FeedsPerDay.ToString("0.#", Strings.Culture), "Title", feedLook.Ink),
                            Ui.Text(Strings.Get("Stats.FeedsCount"), "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    Ui.Text($"Total: {stats.Feeding.TotalFeeds} feeds ({stats.Feeding.BreastFeeds} breast, {stats.Feeding.BottleFeeds} bottle)", "Meta", "Ink2"),
                }
            };
            if (stats.Feeding.BreastFeeds > 0)
            {
                feedCard.Add(Ui.Text($"{Strings.Get("Stats.AvgFeedDuration")}: {Formats.Duration(stats.Feeding.AverageBreastDuration)} (L: {Formats.Duration(stats.Feeding.AverageLeftDuration)} · R: {Formats.Duration(stats.Feeding.AverageRightDuration)})", "Meta", "Ink2"));
            }
            if (stats.Feeding.BottleFeeds > 0)
            {
                feedCard.Add(Ui.Text($"Avg bottle: {Math.Round(stats.Feeding.AverageBottleMl)} ml · Total: {Math.Round(stats.Feeding.TotalBottleMl)} ml", "Meta", "Ink2"));
            }
            cards.Add(new Card(feedCard));

            var diaperLook = EventAppearance.For("diaper");
            var diaperCard = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Rounded(new Icon(diaperLook.Icon, diaperLook.Ink, 20), diaperLook.Tint, 12, new Thickness(6)),
                            Ui.Text(Strings.Get("Kind.Diaper"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Text(stats.Diapers.DiapersPerDay.ToString("0.#", Strings.Culture), "Title", diaperLook.Ink),
                            Ui.Text(Strings.Get("Stats.DiapersCount"), "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                        }
                    },
                    Ui.Text($"Total: {stats.Diapers.TotalDiapers} ({stats.Diapers.WetCount} wet · {stats.Diapers.DirtyCount} dirty · {stats.Diapers.MixedCount} mixed)", "Meta", "Ink2"),
                }
            };
            cards.Add(new Card(diaperCard));

            if (stats.Pumping.TotalSessions > 0)
            {
                var pumpLook = EventAppearance.For("pump");
                var pumpCard = new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        new HorizontalStackLayout
                        {
                            Spacing = 8,
                            Children =
                            {
                                Ui.Rounded(new Icon(pumpLook.Icon, pumpLook.Ink, 20), pumpLook.Tint, 12, new Thickness(6)),
                                Ui.Text(Strings.Get("Kind.Pump"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                            }
                        },
                        new HorizontalStackLayout
                        {
                            Spacing = 8,
                            Children =
                            {
                                Ui.Text($"{Math.Round(stats.Pumping.DailyAverageMl)} ml", "Title", pumpLook.Ink),
                                Ui.Text(Strings.Get("Stats.PumpedVolume"), "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                            }
                        },
                        Ui.Text($"Total: {Math.Round(stats.Pumping.TotalMl)} ml · {stats.Pumping.TotalSessions} sessions (avg {Math.Round(stats.Pumping.AveragePerSessionMl)} ml)", "Meta", "Ink2"),
                    }
                };
                cards.Add(new Card(pumpCard));
            }
        }

        _logContentContainer.Children.Clear();
        _logContentContainer.Add(header);
        _logContentContainer.Add(periodBar);
        _logContentContainer.Add(cards);
    }
}
