using System.ComponentModel;
using Coo.Mobile.Components;
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
    private readonly Sheet _sheet = new();
    private readonly DayDetailView _detail = new();

    public LogPage(LogViewModel model, ThemeController theme, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;
        _language = language;
        _undo = undo;
        _theme = theme;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
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
        if (_body.Content is null)
            Render();
        else
            ShowRibbon();
        if (!_model.CanGoNext)
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

        var header = new Grid { HeightRequest = 48, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(Ui.Text(_model.Title, "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        header.Add(Nav(IconGeometry.ChevronLeft, _model.PreviousCommand, true), 1);
        header.Add(Nav(IconGeometry.ChevronRight, _model.NextCommand, _model.CanGoNext), 2);

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
            var id = row.Id;
            timeline.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who, () => OpenEntry(id)));
        }

        _renderedLanguage = Strings.Language;
        var pageHeader = new CooPageHeader(_services);

        _body.Content = new VerticalStackLayout
        {
            Padding = new Thickness(0, 0, 0, 24),
            Spacing = 16,
            Children = { pageHeader, new VerticalStackLayout { Padding = new Thickness(24, 0, 24, 0), Spacing = 16, Children = { header, _ribbon, legend, totals, timeline } } },
        };

        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current));
    }
}
