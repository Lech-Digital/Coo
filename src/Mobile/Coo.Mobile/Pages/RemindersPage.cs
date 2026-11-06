using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// Settings → Reminders (ADR 0007): the feeding interval, a forgotten timer, the nap window, the diaper check, baths, and
/// medicines and vitamins. Redrawn on each change.
/// </summary>
public sealed class RemindersPage : ContentPage
{
    private readonly RemindersViewModel _model;
    private readonly ContentView _body = new();

    private readonly IServiceProvider _services;

    public RemindersPage(RemindersViewModel model, IServiceProvider services)
    {
        _model = model;
        _services = services;
        _model.PrimeNotificationsAsync = () => Components.CooDialog.PrimeAsync<Permissions.PostNotifications>(this, "Permission.NotifyTitle", "Permission.NotifyBody");
        Shell.SetNavBarIsVisible(this, false);
        Content = Backdrop.Behind(new ScrollView { Content = _body });
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

        var diaper = new VerticalStackLayout { Spacing = 8 };
        diaper.Add(Ui.Text(Strings.Get("Reminders.Diaper"), "BodyStrong"));
        diaper.Add(Ui.Text(Strings.Get("Reminders.DiaperHint"), "Meta", "Ink2"));
        diaper.Add(Chips(RemindersViewModel.DiaperChoices, RemindersViewModel.FeedLabel, settings.DiaperEvery, _model.SetDiaperEveryCommand));
        if (settings.DiaperEvery is not null)
            diaper.Add(Toggle("Reminders.FeedAtNight", _model.QuietHint, settings.DiaperAtNight, _model.ToggleDiaperAtNightCommand));
        stack.Add(new Card(diaper));

        stack.Add(new Card(BathCard(settings)));
        stack.Add(new Card(MedicinesCard(settings)));

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

    /// <summary>Off or a choice, as a row of chips that wraps; the chosen one is the accent.</summary>
    private static FlexLayout Chips<T>(IEnumerable<T> choices, Func<T, string> label, T chosen, System.Windows.Input.ICommand pick)
    {
        var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        foreach (var choice in choices)
        {
            var chip = new Button { Text = label(choice), Command = pick, CommandParameter = choice, Margin = new Thickness(0, 0, 8, 8) };
            chip.SetDynamicResource(StyleProperty, EqualityComparer<T>.Default.Equals(choice, chosen) ? "PrimaryButton" : "GhostButton");
            chips.Add(chip);
        }

        return chips;
    }

    /// <summary>Baths: how often, and at what time of day.</summary>
    private View BathCard(Coo.Mobile.Application.Notifications.ReminderSettings settings)
    {
        var bath = new VerticalStackLayout { Spacing = 8 };
        bath.Add(Ui.Text(Strings.Get("Kind.Bath"), "BodyStrong"));
        bath.Add(Ui.Text(Strings.Get("Reminders.BathHint"), "Meta", "Ink2"));
        bath.Add(Chips(RemindersViewModel.BathChoices, RemindersViewModel.BathLabel, settings.BathEveryDays, _model.SetBathEveryCommand));
        if (settings.BathEveryDays is null)
            return bath;

        var picker = new TimePicker { Time = settings.BathTime.ToTimeSpan(), Format = "HH:mm", VerticalOptions = LayoutOptions.Center };
        picker.SetDynamicResource(TimePicker.TextColorProperty, "Ink");
        picker.TimeSelected += async (_, e) =>
        {
            if (e.NewTime is { } time)
                await _model.SetBathAtAsync(TimeOnly.FromTimeSpan(time));
        };
        var row = new HorizontalStackLayout { Spacing = 12 };
        var at = Ui.Text(Strings.Get("Reminders.BathAt"), "Body", "Ink2");
        at.VerticalOptions = LayoutOptions.Center;
        row.Add(at);
        row.Add(picker);
        bath.Add(row);
        return bath;
    }

    /// <summary>Medicines and vitamins: one row per reminder (tap to change), then "Vitamin D daily" and "Add medicine".</summary>
    private View MedicinesCard(Coo.Mobile.Application.Notifications.ReminderSettings settings)
    {
        var card = new VerticalStackLayout { Spacing = 8 };
        card.Add(Ui.Text(Strings.Get("Reminders.Medicines"), "BodyStrong"));
        card.Add(Ui.Text(Strings.Get("Reminders.MedicinesHint"), "Meta", "Ink2"));
        var medicines = settings.MedicineList;
        for (var i = 0; i < medicines.Count; i++)
        {
            var index = i;
            var medicine = medicines[i];
            var text = new VerticalStackLayout { Spacing = 2 };
            text.Add(Ui.Text(medicine.Dose is { Length: > 0 } dose ? $"{medicine.Name} · {dose}" : medicine.Name, "Body"));
            text.Add(Ui.Text(RemindersViewModel.MedicineLabel(medicine), "Meta", "Ink2"));
            var row = Ui.Rounded(text, "Ground", 14, new Thickness(16, 12), "Line");
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => OpenMedicine(index, medicine)) });
            SemanticProperties.SetDescription(row, medicine.Name);
            card.Add(row);
        }

        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        if (!medicines.Any(m => Coo.Mobile.Application.CareLog.Vitamins.IsVitamin(m.Name)))
        {
            var vitamin = new Button { Text = "+ " + Strings.Get("Reminders.AddVitamin"), Margin = new Thickness(0, 0, 8, 8) };
            vitamin.SetDynamicResource(StyleProperty, "GhostButton");
            vitamin.Clicked += (_, _) => OpenMedicine(-1, new(Strings.Get("MedReminder.VitaminD"), null, [new TimeOnly(9, 0)]));
            buttons.Add(vitamin);
        }

        var add = new Button { Text = "+ " + Strings.Get("Reminders.AddMedicine"), Margin = new Thickness(0, 0, 8, 8) };
        add.SetDynamicResource(StyleProperty, "GhostButton");
        add.Clicked += (_, _) => OpenMedicine(-1, null);
        buttons.Add(add);
        card.Add(buttons);
        return card;
    }

    private async void OpenMedicine(int index, Coo.Mobile.Application.Notifications.MedicineReminder? medicine)
    {
        var today = DateOnly.FromDateTime(_services.GetRequiredService<TimeProvider>().GetLocalNow().DateTime);
        await Navigation.PushAsync(new MedicineReminderPage(medicine, index >= 0, today,
            saved => _model.SaveMedicineAsync(index, saved),
            () => _model.RemoveMedicineAsync(index)));
    }

    /// <summary>A title, a hint and a switch; the whole row toggles.</summary>
    private static Grid Toggle(string titleKey, string hint, bool on, System.Windows.Input.ICommand toggle)
    {
        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        text.Add(Ui.Text(Strings.Get(titleKey), "BodyStrong"));
        text.Add(Ui.Text(hint, "Meta", "Ink2"));
        var box = new Switch { IsToggled = on, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        box.SetDynamicResource(Switch.OnColorProperty, "Accent");
        // A light thumb on the accent by day, a dark one on the light matcha at night (design review, 2026-11-06).
        box.SetDynamicResource(Switch.ThumbColorProperty, "OnAccent");
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        grid.Add(text, 0);
        grid.Add(box, 1);
        grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = toggle });
        SemanticProperties.SetDescription(grid, Strings.Get(titleKey));
        return grid;
    }
}
