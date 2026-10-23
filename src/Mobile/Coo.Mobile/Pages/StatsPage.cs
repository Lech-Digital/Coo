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
    private readonly VerticalStackLayout _content = new() { Padding = new Thickness(24, 0, 24, 0), Spacing = 16 };
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
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = new Thickness(0, 0, 0, 24), Spacing = 16, Children = { _pageHeader, _content } },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Listening only while on screen: the view model outlives this page.
        _trends.PropertyChanged += OnChanged;
        _summary.PropertyChanged += OnChanged;
        _report.PropertyChanged += OnChanged;
        _undo.DataChanged += OnDataChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        _language.Changed += OnLanguageChanged;
        _pageHeader.Refresh();
        await _trends.RefreshAsync();
        if (_content.Children.Count == 0 || _renderedLanguage != Strings.Language)
            QueueRender();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _trends.PropertyChanged -= OnChanged;
        _summary.PropertyChanged -= OnChanged;
        _report.PropertyChanged -= OnChanged;
        _undo.DataChanged -= OnDataChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
        _language.Changed -= OnLanguageChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => QueueRender();

    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        _summary.Invalidate();
        await _trends.RefreshAsync();
    });

    // The chart is drawn with the palette's colours, not resources.
    private void OnPaletteChanged(object? sender, PaletteKind e) => QueueRender();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await _trends.RefreshAsync();
        QueueRender();
    });

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
        header.Add(Ui.Text(Strings.Get("Stats.Title"), "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
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

        _content.Children.Clear();
        _content.Add(header);
        _content.Add(grainBar);
        _content.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = chips, Margin = new Thickness(-24, 0), Padding = new Thickness(24, 0) });
        _content.Add(new Card(headline));
        _content.Add(new Card(rows));
        _content.Add(SummaryCard());
        _content.Add(ReportCard());
    }
}
