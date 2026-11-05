using Coo.Mobile.Application.Growth;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Growth;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>The growth chart (WHO standards): weight, length or head, the latest percentile, and every measurement.</summary>
public sealed class GrowthPage : ContentPage
{
    private readonly GrowthViewModel _model;
    private readonly ThemeController _theme;
    private readonly IServiceProvider _services;
    private readonly ContentView _body = new();

    public GrowthPage(GrowthViewModel model, ThemeController theme, IServiceProvider services)
    {
        (_model, _theme, _services) = (model, theme, services);
        Shell.SetNavBarIsVisible(this, false);
        Content = Backdrop.Behind(new ScrollView { Content = _body });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnChanged;
        _theme.PaletteChanged += OnPaletteChanged;
        await _model.LoadAsync();
        Render();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnChanged;
        _theme.PaletteChanged -= OnPaletteChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(Render);

    private void OnPaletteChanged(object? sender, PaletteKind e) => Dispatcher.Dispatch(Render);

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Growth.Chart"), () => Navigation.PopAsync()));

        if (!_model.HasBirth)
        {
            stack.Add(Ui.Text(Strings.Get("Growth.NoBirth"), "Body", "Ink2"));
            _body.Content = stack;
            return;
        }

        if (_model.Sex is null)
        {
            stack.Add(Ui.Text(Strings.Get("Growth.WhichChart"), "BodyStrong"));
            stack.Add(Ui.Text(Strings.Get("Growth.WhichChartHint"), "Meta", "Ink2"));
            stack.Add(Chips([(Strings.Get("Growth.Girls"), () => _model.SetSexCommand.Execute(ChartSex.Girl), false),
                (Strings.Get("Growth.Boys"), () => _model.SetSexCommand.Execute(ChartSex.Boy), false)]));
            _body.Content = stack;
            return;
        }

        stack.Add(Chips([
            (Strings.Get("Edit.Weight"), () => _model.SetMeasureCommand.Execute(GrowthMeasure.Weight), _model.Measure == GrowthMeasure.Weight),
            (Strings.Get("Edit.Length"), () => _model.SetMeasureCommand.Execute(GrowthMeasure.Length), _model.Measure == GrowthMeasure.Length),
            (Strings.Get("Growth.Head"), () => _model.SetMeasureCommand.Execute(GrowthMeasure.Head), _model.Measure == GrowthMeasure.Head)]));

        if (!string.IsNullOrEmpty(_model.Latest))
            stack.Add(Ui.Text(_model.Latest, "Title"));

        var chart = new GrowthChartView();
        chart.Show(_model.Curves, _model.Points, _model.MaxAge, _model.AxisUnit, Strings.Get("Growth.Months"), WashiPalette.For(_theme.Current));
        stack.Add(new Card(chart));

        var add = new Button { Text = Strings.Get("Growth.Add") };
        add.SetDynamicResource(StyleProperty, "PrimaryButton");
        add.Clicked += async (_, _) =>
        {
            var page = _services.GetRequiredService<EditPage>();
            await page.NewAsync("growth");
            await Navigation.PushAsync(page);
        };
        stack.Add(add);

        if (_model.Rows.Count == 0)
            stack.Add(Ui.Text(Strings.Get("Growth.Empty"), "Body", "Ink2"));
        foreach (var row in _model.Rows)
        {
            var line = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
            line.Add(new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(row.Value, "BodyStrong"), Ui.Text(row.Date, "Meta", "Ink2") } }, 0);
            line.Add(Ui.Text(row.Percentile, "Body", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
            var card = new Card(line);
            card.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    var page = _services.GetRequiredService<EditPage>();
                    await page.OpenAsync(row.Id);
                    await Navigation.PushAsync(page);
                }),
            });
            stack.Add(card);
        }

        // Switching the standard later: a small link, not a setting buried elsewhere.
        var other = _model.Sex == ChartSex.Girl ? ChartSex.Boy : ChartSex.Girl;
        var swap = Ui.Text(Strings.Get(other == ChartSex.Girl ? "Growth.UseGirls" : "Growth.UseBoys"), "Meta", "Accent");
        swap.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _model.SetSexCommand.Execute(other)) });
        stack.Add(swap);
        stack.Add(Ui.Text(Strings.Get("Growth.Source"), "Meta", "Ink2").Tap(l => l.FontSize = 11));
        _body.Content = stack;
    }

    private static FlexLayout Chips((string Text, Action Choose, bool Selected)[] chips)
    {
        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (text, choose, selected) in chips)
        {
            var chip = new Button { Text = text, Margin = new Thickness(0, 0, 8, 8) };
            chip.SetDynamicResource(StyleProperty, selected ? "PrimaryButton" : "GhostButton");
            chip.Clicked += (_, _) => choose();
            row.Add(chip);
        }

        return row;
    }
}
