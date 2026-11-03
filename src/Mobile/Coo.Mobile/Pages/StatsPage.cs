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
    private readonly Coo.Mobile.Presentation.Growth.GrowthViewModel _growth;
    private readonly IServiceProvider _services;
    private readonly VerticalStackLayout _content = new() { Padding = new Thickness(24, 0, 24, 0), Spacing = 16 };
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
        _growth = services.GetRequiredService<Coo.Mobile.Presentation.Growth.GrowthViewModel>();
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
        _page = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = new Thickness(0, 0, 0, 24), Spacing = 16, Children = { _pageHeader, _content } },
        };
        Content = _page;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Listening only while on screen: the view model outlives this page.
        _trends.PropertyChanged += OnChanged;
        _summary.PropertyChanged += OnChanged;
        _report.PropertyChanged += OnChanged;
        _averages.PropertyChanged += OnChanged;
        _growth.PropertyChanged += OnChanged;
        _undo.DataChanged += OnDataChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        _language.Changed += OnLanguageChanged;
        _pageHeader.Refresh();
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync());
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
        _growth.PropertyChanged -= OnChanged;
        _undo.DataChanged -= OnDataChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
        _language.Changed -= OnLanguageChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => QueueRender();

    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        _summary.Invalidate();
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync());
    });

    // The chart is drawn with the palette's colours, not resources.
    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await Task.WhenAll(_trends.RefreshAsync(), _averages.RefreshAsync(), _growth.LoadAsync());
        QueueRender();
    });

    /// <summary>
    /// "On an average day": what a doctor asks first, at the top. Milk from bottles a day and per bottle, feeds, sleep,
    /// diapers, over the last 7, 14 or 30 days.
    /// </summary>
    private Card AveragesCard()
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(Ui.Text(Strings.Get("Averages.Title"), "BodyStrong"));
        var periods = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < AveragesViewModel.Periods.Count; i++)
        {
            var days = AveragesViewModel.Periods[i];
            var chip = new Button { Text = Strings.Format("Report.Days", days), Command = _averages.SetDaysCommand, CommandParameter = days, HeightRequest = 36 };
            chip.SetDynamicResource(VisualElement.StyleProperty, _averages.Days == days ? "PrimaryButton" : "GhostButton");
            chip.Padding = new Thickness(4, 0);
            periods.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            periods.Add(chip, i);
        }

        stack.Add(periods);
        stack.Add(Ui.Text(_averages.Caption, "Meta", "Ink2"));
        foreach (var row in _averages.Rows)
        {
            var look = EventAppearance.For(row.KindCode);
            var grid = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                ColumnSpacing = 12,
                RowSpacing = 2,
                Padding = new Thickness(0, 4),
            };
            grid.Add(Ui.Rounded(new Icon(look.Icon, look.Ink, 16), look.Tint, 10, new Thickness(6)).Tap(b => b.VerticalOptions = LayoutOptions.Center), 0, 0);
            grid.Add(Ui.Text(row.Name, "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1, 0);
            grid.Add(Ui.Text(row.Value, "BodyStrong", look.Ink).Tap(l => l.VerticalOptions = LayoutOptions.Center), 2, 0);
            if (row.Detail.Length > 0)
            {
                var detail = Ui.Text(row.Detail, "Meta", "Ink2");
                grid.Add(detail, 1, 1);
                Grid.SetColumnSpan(detail, 2);
            }

            SemanticProperties.SetDescription(grid, $"{row.Name}: {row.Value}. {row.Detail}");
            stack.Add(grid);
        }

        return new Card(stack);
    }

    /// <summary>Growth among the other numbers: the latest weight and its percentile; a tap opens the chart.</summary>
    private Card? GrowthCard()
    {
        if (!_growth.HasBirth)
            return null;
        var stack = new VerticalStackLayout { Spacing = 6 };
        var title = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        var look = EventAppearance.For("growth");
        title.Add(Ui.Rounded(new Icon(IconGeometry.Chart, look.Ink, 16), look.Tint, 10, new Thickness(6)).Tap(b => b.VerticalOptions = LayoutOptions.Center), 0);
        title.Add(Ui.Text(Strings.Get("Growth.Chart"), "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
        title.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 18).Tap(i => i.VerticalOptions = LayoutOptions.Center), 2);
        stack.Add(title);
        if (_growth.Sex is null)
            stack.Add(Ui.Text(Strings.Get("Growth.WhichChartHint"), "Meta", "Ink2"));
        else if (_growth.Rows.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Growth.Empty"), "Meta", "Ink2"));
        else
            stack.Add(Ui.Text($"{_growth.Latest} · {_growth.Rows[0].Date}", "Body", look.Ink));
        var card = new Card(stack);
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await Navigation.PushAsync(_services.GetRequiredService<GrowthPage>())),
        });
        SemanticProperties.SetDescription(card, Strings.Get("Growth.Chart"));
        return card;
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
            var circle = Ui.Rounded(new Icon(icon, enabled ? "Ink" : "Line", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
            circle.WidthRequest = 48;
            circle.HeightRequest = 48;
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
        header.Add(Ui.Text(Strings.Get("Stats.Trends"), "Section").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        header.Add(Nav(IconGeometry.ChevronLeft, trends.PreviousCommand, true), 1);
        header.Add(Nav(IconGeometry.ChevronRight, trends.NextCommand, trends.CanGoNext), 2);

        var grainBar = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        View GrainChip(string key, TrendGrain grain)
        {
            var chip = new Button { Text = Strings.Get(key), Command = trends.SetGrainCommand, CommandParameter = grain, HeightRequest = 42 };
            chip.SetDynamicResource(VisualElement.StyleProperty, trends.Grain == grain ? "PrimaryButton" : "GhostButton");
            return chip;
        }

        grainBar.Add(GrainChip("Trend.Days", TrendGrain.Day), 0);
        grainBar.Add(GrainChip("Trend.Weeks", TrendGrain.Week), 1);
        grainBar.Add(GrainChip("Trend.Months", TrendGrain.Month), 2);

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
                selected ? chipLook.Tint : "Surface",
                999,
                new Thickness(14, 8),
                selected ? chipLook.Ink : "Line");
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

        var rows = new VerticalStackLayout { Spacing = 0 };
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
                Padding = new Thickness(12, 10),
            };
            grid.Add(Ui.Rounded(new Icon(rowLook.Icon, rowLook.Ink, 16), rowLook.Tint, 10, new Thickness(6)).Tap(b => b.VerticalOptions = LayoutOptions.Center), 0, 0);
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
            rows.Add(row.IsSelected ? Ui.Rounded(grid, rowLook.Tint, 14, new Thickness(0)) : grid);
        }

        // Where the page and the chip row were: a redraw (a chip, a bar, a period) keeps both where the finger left them.
        var (chipX, pageY) = (_chipRow.ScrollX, _page.ScrollY);
        _content.Children.Clear();
        _content.Add(Ui.Text(Strings.Get("Stats.Title"), "Title"));
        _content.Add(AveragesCard());
        if (GrowthCard() is { } growth)
            _content.Add(growth);
        _content.Add(header);
        _content.Add(grainBar);
        _chipRow.Content = chips;
        _content.Add(_chipRow);
        _content.Add(new Card(headline));
        _content.Add(new Card(rows));
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
