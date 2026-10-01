using System.ComponentModel;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Pages;

/// <summary>Log (Spec 006 §7): one day at a time, with the ribbon, totals and the timeline.</summary>
public sealed class LogPage : ContentPage
{
    private readonly LogViewModel _model;
    private readonly ThemeController _theme;
    private readonly ContentView _body = new();

    public LogPage(LogViewModel model, ThemeController theme)
    {
        _model = model;
        _theme = theme;
        Shell.SetNavBarIsVisible(this, false);
        model.PropertyChanged += OnChanged;
        theme.PaletteChanged += (_, _) => Dispatcher.Dispatch(Render);
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.RefreshAsync();
        Render();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.Rows))
            Dispatcher.Dispatch(Render);
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

        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(Ui.Text(_model.Title, "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        header.Add(Nav(IconGeometry.ChevronLeft, _model.PreviousCommand, true), 1);
        header.Add(Nav(IconGeometry.ChevronRight, _model.NextCommand, _model.CanGoNext), 2);

        var ribbon = new RibbonView();
        ribbon.Show(_model.Ribbon, WashiPalette.For(_theme.Current));

        var legend = new HorizontalStackLayout { Spacing = 14 };
        foreach (var (kind, key) in new[] { ("feeding", "Kind.Feed"), ("diaper", "Kind.Diaper"), ("sleep", "Kind.Sleep"), ("pump", "Kind.Pump") })
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
            timeline.Add(new TimelineRow(row.Time, row.KindCode, row.Title, row.Who));

        _body.Content = new VerticalStackLayout
        {
            Padding = new Thickness(24, 40, 24, 24),
            Spacing = 16,
            Children = { header, ribbon, legend, totals, timeline },
        };
    }
}
