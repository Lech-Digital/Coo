using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>Settings → Reminders (ADR 0007): the feeding interval, a forgotten timer, the nap window. Redrawn on each change.</summary>
public sealed class RemindersPage : ContentPage
{
    private readonly RemindersViewModel _model;
    private readonly ContentView _body = new();

    public RemindersPage(RemindersViewModel model)
    {
        _model = model;
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnModelChanged;
        await _model.LoadAsync();
        Render();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnModelChanged;
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(Render);

    private void Render()
    {
        var settings = _model.Settings;
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("Reminders.Title"), () => Navigation.PopAsync()));
        stack.Add(Ui.Text(Strings.Get("Reminders.Intro"), "Body", "Ink2"));

        // Feeding: Off or an interval, as a row of chips that wraps on a narrow phone.
        var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        foreach (var every in RemindersViewModel.FeedChoices)
        {
            var chip = new Button { Text = RemindersViewModel.FeedLabel(every), Command = _model.SetFeedEveryCommand, CommandParameter = every, Margin = new Thickness(0, 0, 8, 8) };
            chip.SetDynamicResource(StyleProperty, every == settings.FeedEvery ? "PrimaryButton" : "GhostButton");
            chips.Add(chip);
        }

        var feed = new VerticalStackLayout { Spacing = 8 };
        feed.Add(Ui.Text(Strings.Get("Reminders.Feed"), "BodyStrong"));
        feed.Add(Ui.Text(Strings.Get("Reminders.FeedHint"), "Meta", "Ink2"));
        feed.Add(chips);
        if (settings.FeedEvery is not null)
            feed.Add(Toggle("Reminders.FeedAtNight", _model.QuietHint, settings.FeedAtNight, _model.ToggleFeedAtNightCommand));
        stack.Add(new Card(feed));

        stack.Add(new Card(Toggle("Reminders.Timer", Strings.Get("Reminders.TimerHint"), settings.TimerRunning, _model.ToggleTimerCommand)));
        stack.Add(new Card(Toggle("Reminders.Nap", Strings.Get("Reminders.NapHint"), settings.NapWindow, _model.ToggleNapCommand)));

        if (!string.IsNullOrEmpty(_model.Message))
            stack.Add(Ui.Text(_model.Message, "Body", "AccentInk"));
        stack.Add(Ui.Text(Strings.Get("Reminders.MayBeLate"), "Meta", "Ink2"));
        _body.Content = stack;
    }

    /// <summary>A title, a hint and a switch; the whole row toggles.</summary>
    private static Grid Toggle(string titleKey, string hint, bool on, System.Windows.Input.ICommand toggle)
    {
        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        text.Add(Ui.Text(Strings.Get(titleKey), "BodyStrong"));
        text.Add(Ui.Text(hint, "Meta", "Ink2"));
        var box = new Switch { IsToggled = on, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        box.SetDynamicResource(Switch.OnColorProperty, "Accent");
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        grid.Add(text, 0);
        grid.Add(box, 1);
        grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = toggle });
        SemanticProperties.SetDescription(grid, Strings.Get(titleKey));
        return grid;
    }
}
