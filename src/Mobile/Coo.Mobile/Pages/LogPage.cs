using System.ComponentModel;
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
    private readonly VerticalStackLayout _logContentContainer = new() { Padding = new Thickness(PageFrame.Side, 0), Spacing = 14 };

    public LogPage(LogViewModel model, ThemeController theme, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;
        _language = language;
        _undo = undo;
        _theme = theme;
        _services = services;
        _pageHeader = new CooPageHeader(services);
        _pageHeader.BabyChanged += () => Dispatcher.Dispatch(async () => await _model.RefreshAsync());
        Shell.SetNavBarIsVisible(this, false);

        var outerStack = new VerticalStackLayout
        {
            Padding = new Thickness(0, 0, 0, 24),
            Spacing = 0, // the title sits as close under the bar as on Today (design review, 2026-11-06)
            Children = { _pageHeader, _logContentContainer }
        };
        _body.Content = outerStack;

        var root = new Grid();
        root.Add(new Backdrop());
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
        _detail.RequestPreviousDay = async () =>
        {
            await _model.PreviousCommand.ExecuteAsync(null);
            _detail.ScrollToEnd();
        };
        _detail.RequestNextDay = async () =>
        {
            if (_model.CanGoNext)
            {
                await _model.NextCommand.ExecuteAsync(null);
                _detail.ScrollToStart();
            }
        };
        _detail.CanGoNext = () => _model.CanGoNext;
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
        await Navigation.PushAsync(await EditPage.ForEntryAsync(_services, id));
    }

    private void Render()
    {
        // Chevron icons, not the ‹ › characters: Figtree has no glyphs for them (Increment 6 finding on the phone).
        View Nav(string icon, System.Windows.Input.ICommand command, bool enabled, string name)
        {
            var circle = Ui.Rounded(new Icon(icon, enabled ? "Ink" : "Ink2", 18).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Glass", 999, new Thickness(11), "GlassLine");
            circle.WidthRequest = 44;
            circle.HeightRequest = 44;
            circle.Opacity = enabled ? 1 : 0.6;
            SemanticProperties.SetDescription(circle, name);
            if (enabled)
                circle.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
            return circle;
        }

        // "Log", then the day under it ("Today · Thursday, 5 November"), the day arrows at the right.
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 6,
        };
        header.Add(PageFrame.Title(Strings.Get("Tab.Log"), $"{_model.Title} · {_model.Subtitle}"), 0);
        header.Add(Nav(IconGeometry.ChevronLeft, _model.PreviousCommand, true, Strings.Get("Log.PreviousDay")).Tap(v => v.VerticalOptions = LayoutOptions.End), 1);
        header.Add(Nav(IconGeometry.ChevronRight, _model.NextCommand, _model.CanGoNext, Strings.Get("Log.NextDay")).Tap(v => v.VerticalOptions = LayoutOptions.End), 2);

        _ribbon = new RibbonView();
        ShowRibbon();
        _ribbon.Tapped += (_, at) => OpenDetail(at);
        SemanticProperties.SetDescription(_ribbon, _model.ChartSummary);
        SemanticProperties.SetHint(_ribbon, Strings.Get("Chart.Hint"));

        var legend = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        // The legend names the kinds that have totals, so hidden kinds leave both (Spec 009 §3.2).
        var names = new Dictionary<string, string> { ["feeding"] = "Kind.Feed", ["diaper"] = "Kind.Diaper", ["sleep"] = "Kind.Sleep", ["pump"] = "Kind.Pump" };
        foreach (var (kind, key) in _model.Totals.Where(t => names.ContainsKey(t.KindCode)).Select(t => (t.KindCode, names[t.KindCode])))
        {
            var dot = new Microsoft.Maui.Controls.Shapes.Ellipse { WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center };
            dot.SetDynamicResource(Microsoft.Maui.Controls.Shapes.Shape.FillProperty, EventAppearance.For(kind).Mark + "Brush");
            legend.Add(new HorizontalStackLayout { Spacing = 5, Margin = new Thickness(0, 0, 14, 0), Children = { dot, Ui.Text(Strings.Get(key), "Meta") } });
        }

        // The day at a glance: the band and its legend on one glass card.
        var band = new Card(new VerticalStackLayout { Spacing = 10, Children = { _ribbon, legend } });

        // Two by two: each card has a breakdown and a comparison under the number (LogTotals).
        var totals = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        var cards = _model.Totals.Select(t => (View)new TotalsCard(t.KindCode, t.Number, t.Unit, t.Detail, t.Compare)).ToList();
        for (var n = 0; n < cards.Count; n++)
        {
            if (n % 2 == 0)
                totals.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            totals.Add(cards[n], n % 2, n / 2);
        }

        // The entries as grouped lists; a midnight line splits the shown day from the evening before.
        var timeline = new VerticalStackLayout { Spacing = 10 };
        if (_model.Rows.Count == 0)
            timeline.Add(Ui.Text(Strings.Get("Timeline.Empty"), "Body", "Ink2"));
        var group = new List<View>();
        void Flush()
        {
            if (group.Count > 0)
                timeline.Add(PageFrame.Group(group));
            group = [];
        }

        foreach (var row in _model.Rows)
        {
            if (row.IsMidnight)
            {
                Flush();
                timeline.Add(new MidnightRow(row.Title));
                continue;
            }

            if (row.Id is { } id)
                group.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who, () => OpenEntry(id), inGroup: true));
        }

        Flush();

        _renderedLanguage = Strings.Language;
                _logContentContainer.Children.Clear();
        _logContentContainer.Add(header);
        _logContentContainer.Add(band);
        _logContentContainer.Add(totals);
        _logContentContainer.Add(timeline);

        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current));
    }
}
