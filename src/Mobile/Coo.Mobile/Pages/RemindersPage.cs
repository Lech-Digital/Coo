using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>Settings → Reminders (ADR 0007): the feeding interval, a forgotten timer, the nap window. Redrawn on each change.</summary>
public sealed class RemindersPage : ContentPage
{
    private readonly RemindersViewModel _model;
    private readonly ContentView _body = new();

    private readonly IServiceProvider _services;

    public RemindersPage(RemindersViewModel model, IServiceProvider services)
    {
        _model = model;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _model.PropertyChanged += OnModelChanged;
        if (Window is { } window)
            window.Resumed += OnResumed;
        await _model.LoadAsync();
        Render();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnModelChanged;
        if (Window is { } window)
            window.Resumed -= OnResumed;
    }

    // Back from Android's "Alarms & reminders": the choice made there shows at once.
    private async void OnResumed(object? sender, EventArgs e) => await _model.LoadAsync();

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
        stack.Add(new Card(NapCard(settings)));

        if (!string.IsNullOrEmpty(_model.Message))
            stack.Add(Ui.Text(_model.Message, "Body", "AccentInk"));
        if (_model.ShowExact)
        {
            var exact = new VerticalStackLayout { Spacing = 8 };
            exact.Add(Ui.Text(Strings.Get("Reminders.Exact"), "BodyStrong"));
            exact.Add(Ui.Text(Strings.Get("Reminders.ExactHint"), "Meta", "Ink2"));
            var allow = new Button { Text = Strings.Get("Reminders.ExactAllow"), Command = _model.AllowExactCommand, HorizontalOptions = LayoutOptions.Start };
            allow.SetDynamicResource(StyleProperty, "GhostButton");
            exact.Add(allow);
            stack.Add(new Card(exact));
        }
        _body.Content = stack;
    }

    /// <summary>
    /// The nap reminder: with Premium a switch and the lead time; without, what it does and the way to Premium, since it
    /// follows the personal prediction (ADR 0008 §7).
    /// </summary>
    private View NapCard(Coo.Mobile.Application.Notifications.ReminderSettings settings)
    {
        if (!_model.IsPremium)
        {
            var locked = Toggle("Reminders.Nap", Strings.Get("Reminders.NapPremium"), false, new Command(async () =>
                await Navigation.PushAsync(_services.GetRequiredService<PremiumPage>())));
            return locked;
        }

        var nap = new VerticalStackLayout { Spacing = 8 };
        nap.Add(Toggle("Reminders.Nap", Strings.Get("Reminders.NapHint"), settings.NapWindow, _model.ToggleNapCommand));
        if (settings.NapWindow)
        {
            var leads = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
            foreach (var minutes in Coo.Mobile.Application.Notifications.ReminderSettings.NapLeads)
            {
                var chip = new Button { Text = RemindersViewModel.LeadLabel(minutes), Command = _model.SetNapLeadCommand, CommandParameter = minutes, Margin = new Thickness(0, 0, 8, 8) };
                chip.SetDynamicResource(StyleProperty, minutes == settings.NapLeadMinutes ? "PrimaryButton" : "GhostButton");
                leads.Add(chip);
            }

            nap.Add(Ui.Text(Strings.Get("Reminders.NapLead"), "Meta", "Ink2"));
            nap.Add(leads);
        }

        return nap;
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
