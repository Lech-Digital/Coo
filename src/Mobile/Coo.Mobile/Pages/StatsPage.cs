using System.ComponentModel;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Stats: daily averages per day, week or month, and how they changed since the one before.</summary>
public sealed class StatsPage : ContentPage
{
    private readonly TrendsViewModel _trends;
    private readonly ThemeController _theme;
    private readonly UndoService _undo;
    private readonly LanguageController _language;
    private readonly CooPageHeader _pageHeader;
    private readonly WeeklySummaryViewModel _summary;
    private readonly Coo.Mobile.Presentation.Reports.ReportViewModel _report;
    private readonly AveragesViewModel _averages;
    private readonly SleepPatternViewModel _pattern;
    private readonly Coo.Mobile.Presentation.MilkStash.MilkStashViewModel _stash;
    private readonly Coo.Mobile.Presentation.Growth.GrowthViewModel _growth;
    private readonly Coo.Mobile.Presentation.SleepPlan.SleepPlanViewModel _plan;
    private readonly IServiceProvider _services;
    private readonly VerticalStackLayout _content = new() { Padding = new Thickness(PageFrame.Side, 0, PageFrame.Side, 24), Spacing = 14 };
    private readonly ScrollView _page;

    /// <summary>
    /// One chip row for the page's life: a new one per redraw started scrolled to the left, so tapping "Pumped" at the
    /// end threw the row back to "Sleep" (owner, 2026-11-03).
    /// </summary>
    private readonly ScrollView _chipRow = new() { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Margin = new Thickness(-24, 0), Padding = new Thickness(24, 0) };
    private bool _renderQueued;
    private string? _renderedLanguage;

    public StatsPage(TrendsViewModel trends, ThemeController theme, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _trends = trends;
        _theme = theme;
        _undo = undo;
        _language = language;
        _pageHeader = new CooPageHeader(services);
        _summary = services.GetRequiredService<WeeklySummaryViewModel>();
        _summary.EnsureConsentAsync = () => AdvisorPage.AskConsentAsync(this);
        _report = services.GetRequiredService<Coo.Mobile.Presentation.Reports.ReportViewModel>();
        _averages = services.GetRequiredService<AveragesViewModel>();
        _pattern = services.GetRequiredService<SleepPatternViewModel>();
        _growth = services.GetRequiredService<Coo.Mobile.Presentation.Growth.GrowthViewModel>();
        _plan = services.GetRequiredService<Coo.Mobile.Presentation.SleepPlan.SleepPlanViewModel>();
        _stash = services.GetRequiredService<Coo.Mobile.Presentation.MilkStash.MilkStashViewModel>();
        _pageHeader.BabyChanged += () => Dispatcher.Dispatch(async () => await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync(), _plan.LoadAsync(), _stash.LoadAsync(), _pattern.RefreshAsync()));
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
        _page = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = new Thickness(0, 0, 0, 24), Spacing = 16, Children = { _pageHeader, _content } },
        };
        Content = Backdrop.Behind(_page);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Listening only while on screen: the view model outlives this page.
        _trends.PropertyChanged += OnChanged;
        _summary.PropertyChanged += OnChanged;
        _report.PropertyChanged += OnChanged;
        _averages.PropertyChanged += OnChanged;
        _pattern.PropertyChanged += OnChanged;
        _growth.PropertyChanged += OnChanged;
        _plan.PropertyChanged += OnChanged;
        _stash.PropertyChanged += OnChanged;
        _undo.DataChanged += OnDataChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        _language.Changed += OnLanguageChanged;
        _pageHeader.Refresh();
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync(), _plan.LoadAsync(), _stash.LoadAsync(), _pattern.RefreshAsync());
        if (_content.Children.Count == 0 || _renderedLanguage != Strings.Language)
            QueueRender();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _trends.PropertyChanged -= OnChanged;
        _summary.PropertyChanged -= OnChanged;
        _report.PropertyChanged -= OnChanged;
        _averages.PropertyChanged -= OnChanged;
        _pattern.PropertyChanged -= OnChanged;
        _growth.PropertyChanged -= OnChanged;
        _plan.PropertyChanged -= OnChanged;
        _stash.PropertyChanged -= OnChanged;
        _undo.DataChanged -= OnDataChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
        _language.Changed -= OnLanguageChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => QueueRender();

    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        _summary.Invalidate();
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync(), _plan.LoadAsync(), _stash.LoadAsync(), _pattern.RefreshAsync());
    });

    // The chart is drawn with the palette's colours, not resources.
    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync(), _plan.LoadAsync(), _stash.LoadAsync(), _pattern.RefreshAsync());
        QueueRender();
    });

    /// <summary>A badge of a kind's colours behind its icon (the rows of Stats).</summary>
    private static View Badge(string kindCode, string? icon = null)
    {
        var look = EventAppearance.For(kindCode);
        var badge = Ui.Rounded(new Icon(icon ?? look.Icon, look.Ink, 18), look.Tint, 10, new Thickness(7));
        badge.WidthRequest = 32;
        badge.HeightRequest = 32;
        return badge;
    }

    /// <summary>
    /// "On an average day": what a doctor asks first, at the top. Milk from bottles a day and per bottle, feeds, sleep,
    /// diapers, over the last 7, 14 or 30 days; the period choice, then the kinds as a grouped list, each with its icon (owner, 2026-11-05).
    /// </summary>
    private IEnumerable<View> Averages()
    {
        var periods = AveragesViewModel.Periods;
        yield return PageFrame.Segmented(
            [.. periods.Select(d => Strings.Format("Report.Days", d))],
            Math.Max(0, periods.ToList().IndexOf(_averages.Days)),
            i => _averages.SetDaysCommand.Execute(periods[i]));
        if (_averages.Rows.Count == 0)
            yield break;
        yield return PageFrame.SectionLabel(Strings.Get("Averages.Title"));
        var rows = new List<View>();
        foreach (var row in _averages.Rows)
        {
            var look = EventAppearance.For(row.KindCode);
            var grid = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = 12,
                Padding = new Thickness(16, 12),
            };
            grid.Add(Badge(row.KindCode).Tap(v => v.VerticalOptions = LayoutOptions.Center), 0);
            var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
            text.Add(Ui.Text(row.Name, "Body").Tap(l => l.FontFamily = "FigtreeMedium"));
            if (row.Detail.Length > 0)
                text.Add(Ui.Text(row.Detail, "Meta", "Ink2"));
            grid.Add(text, 1);
            grid.Add(Ui.Text(row.Value, "BodyStrong", look.Ink).Tap(l =>
            {
                l.VerticalOptions = LayoutOptions.Center;
                l.StyleClass = [Tabular.StyleClass];
            }), 2);
            SemanticProperties.SetDescription(grid, $"{row.Name}: {row.Value}. {row.Detail}");
            rows.Add(grid);
        }

        yield return PageFrame.Group(rows);
    }

    /// <summary>
    /// The sleep pattern (owner, 2026-11-06): the days side by side, noon to noon, so the rhythm shows by eye, and when her
    /// longest sleep usually starts.
    /// </summary>
    private static readonly int[] PatternHours = [12, 18, 0, 6, 12];

    private IEnumerable<View> Pattern()
    {
        if (_pattern.Pattern is not { } pattern)
            yield break;
        yield return PageFrame.SectionLabel(Strings.Get("Pattern.Title"));
        var periods = SleepPatternViewModel.Periods;
        yield return PageFrame.Segmented(
            [.. periods.Select(d => Strings.Format("Report.Days", d))],
            Math.Max(0, periods.ToList().IndexOf(_pattern.Days)),
            i => _pattern.SetDaysCommand.Execute(periods[i]));

        // Under each column its weekday for a week, its date otherwise; at 30 days every fifth, and the last.
        var count = pattern.Days.Count;
        var labels = pattern.Days.Select((d, i) => count <= 7
            ? d.Evening.ToString("ddd", Strings.Culture)
            : (count <= 14 || i % 5 == (count - 1) % 5) ? d.Evening.Day.ToString(Strings.Culture) : "").ToList();
        var is24Hour = _services.GetRequiredService<Coo.Mobile.Application.Preferences.IClockFormat>().Is24Hour;
        string[] hours = [.. PatternHours.Select(h => is24Hour
            ? new TimeOnly(h, 0).ToString("HH:mm", Strings.Culture)
            : new DateTime(2000, 1, 1, h, 0, 0).ToString("h tt", Strings.Culture))];
        var chart = new PatternChartView();
        // Diapers as dots up to 14 days; at 30 the columns are too narrow for them.
        var diapers = _pattern.DiapersShown && count <= 14;
        chart.Show(pattern, labels, hours, _pattern.FeedsShown, WashiPalette.For(_theme.Current), diapers);
        chart.Tapped += async (_, day) =>
        {
            // A column opens that day in the Log.
            _services.GetRequiredService<LogViewModel>().Show(day);
            await Shell.Current.GoToAsync("//log");
        };
        SemanticProperties.SetDescription(chart, _pattern.Summary);

        var legend = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        void Key(string colour, double alpha, string text)
        {
            var swatch = new BoxView { WidthRequest = 10, HeightRequest = 10, CornerRadius = 3, VerticalOptions = LayoutOptions.Center, Opacity = alpha };
            swatch.SetDynamicResource(BoxView.ColorProperty, colour);
            legend.Add(new HorizontalStackLayout { Spacing = 5, Margin = new Thickness(0, 0, 14, 4), Children = { swatch, Ui.Text(text, "Meta") } });
        }

        Key("SleepMark", 1, Strings.Get("Pattern.Longest"));
        Key("SleepMark", 0.5, Strings.Get("Kind.Sleep"));
        if (_pattern.FeedsShown)
            Key("FeedMark", 1, Strings.Get("Kind.Feed"));
        if (diapers)
            Key("DiaperMark", 1, Strings.Get("Kind.Diaper"));

        yield return new Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Text(_pattern.Summary, "Body"),
                chart,
                legend,
                Ui.Text(Strings.Get("Pattern.Hint"), "Meta", "Ink2"),
            },
        });
    }

    /// <summary>"More": growth, the sleep plan and the milk stash as rows that open their pages, each when it applies.</summary>
    private IEnumerable<View> More()
    {
        var rows = new List<View>();
        if (_growth.HasBirth)
        {
            var line = _growth.Sex is null ? Strings.Get("Growth.WhichChartHint")
                : _growth.Rows.Count == 0 ? Strings.Get("Growth.Empty")
                : $"{_growth.Latest} · {_growth.Rows[0].Date}";
            rows.Add(PageFrame.Row(Strings.Get("Growth.Chart"), line, open: () => Navigation.PushAsync(_services.GetRequiredService<GrowthPage>()), leading: Badge("growth", IconGeometry.Chart)));
        }

        if (_plan.IsReady && _plan.IsBorn)
        {
            var line = _plan.CurrentLine() ?? (_plan.CanChange ? Strings.Get("Plan.StartCta") : Strings.Get("Plan.ReadOnly"));
            rows.Add(PageFrame.Row(Strings.Get("Kind.SleepPlan"), line, open: () => Navigation.PushAsync(_services.GetRequiredService<SleepPlanPage>()), leading: Badge("sleepplan", IconGeometry.Sleep)));
        }

        if (_stash.IsReady && _stash.IsPumpShown)
        {
            var line = string.Join(" · ", new[] { _stash.Totals.Length > 0 ? _stash.Totals : Strings.Get("Stash.Hint"), _stash.Warning }.Where(t => t.Length > 0));
            rows.Add(PageFrame.Row(Strings.Get("Stash.Open"), line, open: () => Navigation.PushAsync(_services.GetRequiredService<MilkStashPage>()), leading: Badge("milkbag")));
        }

        if (rows.Count == 0)
            yield break;
        yield return PageFrame.SectionLabel(Strings.Get("Stats.More"));
        yield return PageFrame.Group(rows);
    }

    /// <summary>Twins and siblings: the average day for each born baby, a column each, for the period chosen above.</summary>
    private Card? SideBySideCard()
    {
        if (_averages.CompareNames.Count < 2 || _averages.Compare.Count == 0)
            return null;
        var names = _averages.CompareNames;
        var table = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < names.Count; i++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            table.Add(Ui.Text(names[i], "Meta", "Ink2").Tap(l => { l.MaxLines = 1; l.LineBreakMode = LineBreakMode.TailTruncation; l.MaximumWidthRequest = 90; }), i + 1, 0);
        }

        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var row = 1;
        foreach (var measure in _averages.Compare)
        {
            var look = EventAppearance.For(measure.KindCode);
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            table.Add(Ui.Text(measure.Name, "Body"), 0, row);
            for (var i = 0; i < measure.Values.Count; i++)
                table.Add(Ui.Text(measure.Values[i], "BodyStrong", look.Ink), i + 1, row);
            SemanticProperties.SetDescription(table, string.Join(". ", _averages.Compare.Select(m => $"{m.Name}: {string.Join(", ", names.Zip(m.Values, (n, v) => $"{n} {v}"))}")));
            row++;
        }

        return new Card(new VerticalStackLayout { Spacing = 10, Children = { Ui.Text(Strings.Get("Averages.SideBySide"), "BodyStrong"), table } });
    }

    /// <summary>"This week, in words": a button until written, then the Advisor's few sentences.</summary>
    private Card SummaryCard()
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Icon(IconGeometry.Sparkle, "Accent", 18).Tap(i => i.VerticalOptions = LayoutOptions.Center),
                Ui.Text(Strings.Get("Summary.Title"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
            },
        });
        if (_summary.IsWriting)
            stack.Add(Ui.Text(Strings.Get("Summary.Writing"), "Body", "Ink2"));
        else if (_summary.Text.Length > 0)
            stack.Add(Ui.Text(_summary.Text, "Body", _summary.IsError ? "AccentInk" : "Ink"));
        else
            stack.Add(Ui.Text(Strings.Get("Summary.Hint"), "Meta", "Ink2"));

        if (!_summary.IsWriting && (_summary.Text.Length == 0 || _summary.IsError))
        {
            var write = new Button { Text = Strings.Get("Summary.Write"), Command = _summary.WriteCommand, HorizontalOptions = LayoutOptions.Start };
            write.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
            stack.Add(write);
        }

        return new Card(stack);
    }

    /// <summary>"Report for the doctor": 7, 14 or 30 days, then the PDF in the share sheet.</summary>
    private Card ReportCard()
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Icon(IconGeometry.Health, "Accent", 18).Tap(i => i.VerticalOptions = LayoutOptions.Center),
                Ui.Text(Strings.Get("Report.Button"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
            },
        });
        stack.Add(Ui.Text(Strings.Get("Report.Hint"), "Meta", "Ink2"));

        var periods = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        for (var i = 0; i < Coo.Mobile.Presentation.Reports.ReportViewModel.Periods.Count; i++)
        {
            var days = Coo.Mobile.Presentation.Reports.ReportViewModel.Periods[i];
            var chip = new Button { Text = Strings.Format("Report.Days", days), Command = _report.SetDaysCommand, CommandParameter = days, HeightRequest = 40 };
            chip.SetDynamicResource(VisualElement.StyleProperty, _report.Days == days ? "PrimaryButton" : "GhostButton");
            // Set after the style, whose padding is for wide buttons: "14 days" must fit a third of the card.
            chip.Padding = new Thickness(4, 0);
            periods.Add(chip, i);
        }

        stack.Add(periods);

        // The report's language: the app's by default, or the doctor's.
        var languages = Coo.Mobile.Presentation.Reports.ReportViewModel.Languages;
        var picker = new Picker { Title = Strings.Get("Report.Language"), FontSize = 15, FontFamily = "FigtreeRegular" };
        foreach (var language in languages)
            picker.Items.Add(Strings.NativeName(language));
        picker.SelectedIndex = Math.Max(0, languages.ToList().IndexOf(_report.Language));
        picker.SetDynamicResource(Picker.TextColorProperty, "Ink");
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
                _report.SetLanguageCommand.Execute(languages[picker.SelectedIndex]);
        };
        var languageRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        languageRow.Add(Ui.Text(Strings.Get("Report.Language"), "Body", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        languageRow.Add(Ui.Rounded(picker, "Surface", 12, new Thickness(12, 0), "Line"), 1);
        stack.Add(languageRow);

        var make = new Button
        {
            Text = Strings.Get(_report.IsMaking ? "Report.Making" : "Report.Make"),
            Command = _report.MakeCommand,
            IsEnabled = !_report.IsMaking,
        };
        make.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        stack.Add(make);
        if (!string.IsNullOrEmpty(_report.Message))
            stack.Add(Ui.Text(_report.Message, "Body", "AccentInk"));
        return new Card(stack);
    }

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

    /// <summary>
    /// The statistics: a bar chart of one metric's daily average per day, week or month, and the selected bar compared
    /// with the one before it. A tap on a chip or a row charts that metric; a tap on a bar compares that bar.
    /// </summary>
    private void Render()
    {
        _renderedLanguage = Strings.Language;
        var trends = _trends;

        View Nav(string icon, System.Windows.Input.ICommand command, bool enabled)
        {
            var circle = Ui.Rounded(new Icon(icon, enabled ? "Ink" : "Ink2", 18).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Glass", 999, new Thickness(11), "GlassLine");
            circle.WidthRequest = 44;
            circle.HeightRequest = 44;
            circle.Opacity = enabled ? 1 : 0.6;
            if (enabled)
                circle.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
            return circle;
        }

        var header = new Grid
        {
            MinimumHeightRequest = 48,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8,
            VerticalOptions = LayoutOptions.Center,
        };
        // The arrows page through the trends, so they sit on the trends' own heading, under the averages and growth.
        header.Add(PageFrame.SectionLabel(Strings.Get("Stats.Trends")).Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        header.Add(Nav(IconGeometry.ChevronLeft, trends.PreviousCommand, true), 1);
        header.Add(Nav(IconGeometry.ChevronRight, trends.NextCommand, trends.CanGoNext), 2);

        TrendGrain[] grains = [TrendGrain.Day, TrendGrain.Week, TrendGrain.Month];
        var grainBar = PageFrame.Segmented(
            [Strings.Get("Trend.Days"), Strings.Get("Trend.Weeks"), Strings.Get("Trend.Months")],
            Array.IndexOf(grains, trends.Grain),
            i => trends.SetGrainCommand.Execute(grains[i]));

        // The metrics as chips under the grains, so switching needs no scrolling down to the rows (owner's request).
        var chips = new HorizontalStackLayout { Spacing = 8 };
        foreach (var metric in trends.Metrics)
        {
            var chipLook = EventAppearance.For(TrendsViewModel.KindOf(metric));
            var selected = metric == trends.Metric;
            var chip = Ui.Rounded(
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children =
                    {
                        new Icon(chipLook.Icon, chipLook.Ink, 16).Tap(i => i.VerticalOptions = LayoutOptions.Center),
                        Ui.Text(TrendsViewModel.ChipOf(metric), selected ? "BodyStrong" : "Body", selected ? chipLook.Ink : "Ink").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                    },
                },
                selected ? chipLook.Tint : "Glass",
                999,
                new Thickness(14, 8),
                selected ? chipLook.Ink : "GlassLine");
            var tapped = metric;
            chip.GestureRecognizers.Add(new TapGestureRecognizer { Command = trends.SetMetricCommand, CommandParameter = tapped });
            SemanticProperties.SetDescription(chip, TrendsViewModel.NameOf(metric));
            chips.Add(chip);
        }

        var look = EventAppearance.For(trends.KindCode);
        // A new chart each time, like the ribbon: a view still in the old layout cannot join the new one (crashed on the Pixel 10).
        var chart = new TrendChartView();
        chart.Tapped += (_, index) => trends.SelectCommand.Execute(index);
        chart.Show(trends.Bars, trends.Average, trends.KindCode, WashiPalette.For(_theme.Current));
        SemanticProperties.SetDescription(chart, $"{trends.MetricName}: {trends.Value}. {trends.Change}");

        var headline = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Rounded(new Icon(look.Icon, look.Ink, 18), look.Tint, 10, new Thickness(5)),
                        Ui.Text(trends.MetricName, "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center),
                    },
                },
                Ui.Text(trends.Value, "Title", look.Ink),
                Ui.Text(trends.Change, "Meta", "Ink"),
                Ui.Text(string.IsNullOrEmpty(trends.PeriodCaption) ? trends.PeriodTitle : $"{trends.PeriodTitle} · {trends.PeriodCaption}", "Meta", "Ink2"),
                chart,
                Ui.Text(string.IsNullOrEmpty(trends.AverageText) ? Strings.Get("Trend.Hint") : $"- - {trends.AverageText} · {Strings.Get("Trend.Hint")}", "Meta", "Ink2"),
            },
        };

        var rows = new List<View>();
        foreach (var row in trends.Rows)
        {
            var rowLook = EventAppearance.For(row.KindCode);
            // The change goes on a line of its own: beside the value it squeezed the name to a letter a line (Pixel 10).
            var grid = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                ColumnSpacing = 12,
                RowSpacing = 2,
                Padding = new Thickness(16, 10),
            };
            if (row.IsSelected)
                grid.SetDynamicResource(BackgroundColorProperty, rowLook.Tint);
            grid.Add(Badge(row.KindCode).Tap(b => b.VerticalOptions = LayoutOptions.Center), 0, 0);
            grid.Add(Ui.Text(row.Name, row.IsSelected ? "BodyStrong" : "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1, 0);
            grid.Add(Ui.Text(row.Value, "BodyStrong", rowLook.Ink).Tap(l => l.VerticalOptions = LayoutOptions.Center), 2, 0);
            var line = string.Join(" · ", new[] { row.Detail, row.Change }.Where(t => !string.IsNullOrEmpty(t)));
            if (line.Length > 0)
            {
                var change = Ui.Text(line, "Meta", "Ink2");
                grid.Add(change, 1, 1);
                Grid.SetColumnSpan(change, 2);
            }

            var metric = row.Metric;
            grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = trends.SetMetricCommand, CommandParameter = metric });
            rows.Add(grid);
        }

        // Where the page and the chip row were: a redraw (a chip, a bar, a period) keeps both where the finger left them.
        var (chipX, pageY) = (_chipRow.ScrollX, _page.ScrollY);
        _content.Children.Clear();
        _content.Add(PageFrame.Title(Strings.Get("Tab.Stats"), _averages.Caption));
        foreach (var view in Averages())
            _content.Add(view);
        if (SideBySideCard() is { } twins)
            _content.Add(twins);
        foreach (var view in Pattern())
            _content.Add(view);
        foreach (var view in More())
            _content.Add(view);
        _content.Add(header);
        _content.Add(grainBar);
        _chipRow.Content = chips;
        _content.Add(_chipRow);
        _content.Add(new Card(headline));
        if (rows.Count > 0)
            _content.Add(PageFrame.Group(rows));
        _content.Add(SummaryCard());
        _content.Add(ReportCard());
        Dispatcher.Dispatch(async () =>
        {
            if (chipX > 0)
                await _chipRow.ScrollToAsync(chipX, 0, false);
            if (pageY > 0)
                await _page.ScrollToAsync(0, pageY, false);
        });
    }
}
