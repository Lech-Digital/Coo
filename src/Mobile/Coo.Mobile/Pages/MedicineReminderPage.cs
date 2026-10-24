using Coo.Mobile.Application.Notifications;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Pages;

/// <summary>
/// One medicine or vitamin reminder: its name and dose as on the bottle, the times of day (up to
/// <see cref="MedicineReminder.MaxTimes"/>), and for how long, every day or a course of some days (an antibiotic for a
/// week). Saving and removing go back to Reminders.
/// </summary>
public sealed class MedicineReminderPage : ContentPage
{
    private static readonly int?[] Durations = [null, 3, 5, 7, 10, 14];

    private readonly bool _existing;
    private readonly DateOnly _today;
    private readonly Func<MedicineReminder, Task> _save;
    private readonly Func<Task> _remove;
    private readonly ContentView _body = new();
    private readonly Entry _name;
    private readonly Entry _dose;
    private readonly List<TimeOnly> _times;
    private int? _days;
    private string _error = "";

    public MedicineReminderPage(MedicineReminder? medicine, bool existing, DateOnly today, Func<MedicineReminder, Task> save, Func<Task> remove)
    {
        _existing = existing;
        _today = today;
        _save = save;
        _remove = remove;
        _name = Field(medicine?.Name, Strings.Get("MedReminder.Name"), 60);
        _dose = Field(medicine?.Dose, "400 IU · 2.5 ml", 40);
        _times = medicine is { Times.Count: > 0 } ? [.. medicine.Times.Order()] : [new TimeOnly(9, 0)];
        _days = medicine?.Until is { } until ? until.DayNumber - today.DayNumber + 1 : null;
        if (_days is <= 0)
            _days = null;
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        Render();
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.BackHeader(Strings.Get("MedReminder.Title"), () => Navigation.PopAsync()));

        stack.Add(Ui.Text(Strings.Get("MedReminder.Name"), "Meta", "Ink2"));
        stack.Add(Ui.Rounded(_name, "Surface", 12, new Thickness(16, 4), "Line"));
        stack.Add(Ui.Text(Strings.Get("MedReminder.Dose"), "Meta", "Ink2"));
        stack.Add(Ui.Rounded(_dose, "Surface", 12, new Thickness(16, 4), "Line"));

        // The times: each a picker with a remove button (but the last one stays), then "Add a time".
        stack.Add(Ui.Text(Strings.Get("MedReminder.Times"), "Meta", "Ink2"));
        for (var i = 0; i < _times.Count; i++)
        {
            var index = i;
            var picker = new TimePicker { Time = _times[i].ToTimeSpan(), Format = "HH:mm", VerticalOptions = LayoutOptions.Center };
            picker.SetDynamicResource(TimePicker.TextColorProperty, "Ink");
            picker.TimeSelected += (_, e) =>
            {
                if (e.NewTime is { } time)
                    _times[index] = TimeOnly.FromTimeSpan(time);
            };
            var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
            row.Add(Ui.Rounded(picker, "Surface", 12, new Thickness(16, 2), "Line"), 0);
            if (_times.Count > 1)
            {
                var remove = new Button { Text = "✕", WidthRequest = 48 };
                remove.SetDynamicResource(StyleProperty, "GhostButton");
                remove.Clicked += (_, _) =>
                {
                    _times.RemoveAt(index);
                    Render();
                };
                SemanticProperties.SetDescription(remove, Strings.Get("Action.Remove"));
                row.Add(remove, 1);
            }

            stack.Add(row);
        }

        if (_times.Count < MedicineReminder.MaxTimes)
        {
            var add = new Button { Text = "+ " + Strings.Get("MedReminder.AddTime"), HorizontalOptions = LayoutOptions.Start };
            add.SetDynamicResource(StyleProperty, "GhostButton");
            add.Clicked += (_, _) =>
            {
                // The next one a few hours after the last, as a course is spread over the day.
                var last = _times.Max();
                _times.Add(last.AddHours(last.Hour < 18 ? 6 : 1));
                Render();
            };
            stack.Add(add);
        }

        stack.Add(Ui.Text(Strings.Get("MedReminder.For"), "Meta", "Ink2"));
        var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        foreach (var days in _days is { } d && !Durations.Contains(d) ? [.. Durations, d] : Durations)
        {
            var chip = new Button
            {
                Text = days is { } n ? Strings.Format("MedReminder.Days", n) : Strings.Get("MedReminder.Ongoing"),
                Margin = new Thickness(0, 0, 8, 8),
            };
            chip.SetDynamicResource(StyleProperty, days == _days ? "PrimaryButton" : "GhostButton");
            chip.Clicked += (_, _) =>
            {
                _days = days;
                Render();
            };
            chips.Add(chip);
        }

        stack.Add(chips);
        if (_error.Length > 0)
            stack.Add(Ui.Text(_error, "Body", "FeedInk"));

        var save = new Button { Text = Strings.Get("Action.Save") };
        save.SetDynamicResource(StyleProperty, "PrimaryButton");
        save.Clicked += async (_, _) => await SaveAsync();
        stack.Add(save);
        if (_existing)
        {
            var remove = new Button { Text = Strings.Get("MedReminder.Remove") };
            remove.SetDynamicResource(StyleProperty, "GhostButton");
            remove.Clicked += async (_, _) =>
            {
                await _remove();
                await Navigation.PopAsync();
            };
            stack.Add(remove);
        }

        _body.Content = stack;
    }

    private async Task SaveAsync()
    {
        var name = _name.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            _error = Strings.Get("MedReminder.NameNeeded");
            Render();
            return;
        }

        var dose = string.IsNullOrWhiteSpace(_dose.Text) ? null : _dose.Text.Trim();
        var until = _days is { } days ? _today.AddDays(days - 1) : (DateOnly?)null;
        await _save(new MedicineReminder(name, dose, [.. _times.Distinct().Order()], until));
        await Navigation.PopAsync();
    }

    private static Entry Field(string? text, string placeholder, int length)
    {
        var entry = new Entry { Text = text, Placeholder = placeholder, FontFamily = "FigtreeRegular", FontSize = 16, MaxLength = length };
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        return entry;
    }
}
