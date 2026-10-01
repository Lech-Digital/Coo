using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Edit;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>One entry's form (Spec 007 §3): edit, or log earlier. A pushed page, so the keyboard and Back just work.</summary>
public sealed class EditPage : ContentPage
{
    private readonly EditViewModel _model;
    private readonly ContentView _body = new();

    public EditPage(EditViewModel model)
    {
        _model = model;
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.SoftInput);
        model.Closed += async (_, _) => await Navigation.PopAsync();
        Content = new ScrollView { Content = _body };
    }

    /// <summary>
    /// Back returns to the page below. Shell did not pop pushed pages on Back (found on the Pixel 10 in Increment 7):
    /// Coo went to the background instead.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    /// <summary>Opens an existing entry.</summary>
    public async Task OpenAsync(Coo.Domain.Common.EntryId id)
    {
        await _model.LoadAsync(id);
        Render();
    }

    /// <summary>Opens an empty form for "Earlier…".</summary>
    public async Task NewAsync(string kindCode, bool bottle = false)
    {
        await _model.NewAsync(kindCode);
        _model.IsBottleForm = bottle;
        Render();
    }

    private void Render()
    {
        var look = EventAppearance.For(_model.KindCode);
        var back = Ui.Rounded(new Icon(IconGeometry.ChevronLeft, "Ink", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
        back.WidthRequest = back.HeightRequest = 48;
        back.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Navigation.PopAsync()) });
        var title = Ui.Text(Strings.Get(KindKey()), "Title");
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        header.Add(back, 0);
        header.Add(title.Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);

        var form = new VerticalStackLayout { Spacing = 14 };
        form.Add(header);

        if (!_model.IsReadable)
        {
            form.Add(Ui.Text(_model.UnreadableText, "Body", "Ink2"));
            form.Add(Field(Strings.Get("Edit.Time"), Ui.Text($"{_model.StartDate:d} {_model.StartTime:hh\\:mm}", "BodyStrong")));
            form.Add(DeleteButton());
            _body.Content = Wrap(form);
            return;
        }

        form.Add(Field(_model.KindCode is "sleep" or "pump" || (_model.KindCode == "feeding" && !_model.IsBottle) ? Strings.Get("Edit.Start") : Strings.Get("Edit.Time"),
            DateAndTime(nameof(EditViewModel.StartDate), nameof(EditViewModel.StartTime))));

        switch (_model.KindCode)
        {
            case "feeding" when _model.IsBottle:
                form.Add(Choices(new (string, Action, bool)[]
                {
                    (Strings.Get("Milk.Formula"), () => _model.Milk = BottleMilk.Formula, _model.Milk == BottleMilk.Formula),
                    (Strings.Get("Milk.Breast"), () => _model.Milk = BottleMilk.Breast, _model.Milk == BottleMilk.Breast),
                    (Strings.Get("Milk.Both"), () => _model.Milk = BottleMilk.Both, _model.Milk == BottleMilk.Both),
                }));
                form.Add(Field($"{(_model.Milk == BottleMilk.Both ? Strings.Get("Milk.Breast") : Strings.Get("Edit.Total"))} ({_model.UnitLabel})", Number(nameof(EditViewModel.Amount))));
                if (_model.Milk == BottleMilk.Both)
                    form.Add(Field($"{Strings.Get("Milk.Formula")} ({_model.UnitLabel})", Number(nameof(EditViewModel.SecondAmount))));
                break;
            case "feeding" when !_model.IsRunning:
                form.Add(Field(Strings.Get("Edit.FirstSide"), Choices(new (string, Action, bool)[]
                {
                    (Strings.Get("Feed.Left"), () => _model.FirstSide = BreastSide.Left, _model.FirstSide == BreastSide.Left),
                    (Strings.Get("Feed.Right"), () => _model.FirstSide = BreastSide.Right, _model.FirstSide == BreastSide.Right),
                })));
                form.Add(Field(Minutes("Feed.Left"), Number(nameof(EditViewModel.LeftMinutes))));
                form.Add(Field(Minutes("Feed.Right"), Number(nameof(EditViewModel.RightMinutes))));
                break;
            case "diaper":
                form.Add(Choices(new (string, Action, bool)[]
                {
                    (Strings.Get("Diaper.Wet"), () => _model.Contents = DiaperContents.Wet, _model.Contents == DiaperContents.Wet),
                    (Strings.Get("Diaper.Dirty"), () => _model.Contents = DiaperContents.Dirty, _model.Contents == DiaperContents.Dirty),
                    (Strings.Get("Diaper.Mixed"), () => _model.Contents = DiaperContents.Mixed, _model.Contents == DiaperContents.Mixed),
                }));
                break;
            case "sleep" or "pump":
                if (!_model.IsRunning)
                    form.Add(Field(Strings.Get("Edit.End"), DateAndTime(nameof(EditViewModel.EndDate), nameof(EditViewModel.EndTime))));
                if (_model.KindCode == "sleep")
                {
                    form.Add(Choices(new (string, Action, bool)[]
                    {
                        (Strings.Get("Edit.KindAuto"), () => _model.SleepKind = null, _model.SleepKind is null),
                        (Strings.Get("Edit.KindNap"), () => _model.SleepKind = SleepKindOverride.Nap, _model.SleepKind == SleepKindOverride.Nap),
                        (Strings.Get("Edit.KindNight"), () => _model.SleepKind = SleepKindOverride.Night, _model.SleepKind == SleepKindOverride.Night),
                    }));
                }
                else
                {
                    form.Add(Field($"{Strings.Get("Feed.Left")} ({_model.UnitLabel})", Number(nameof(EditViewModel.PumpLeft))));
                    form.Add(Field($"{Strings.Get("Feed.Right")} ({_model.UnitLabel})", Number(nameof(EditViewModel.PumpRight))));
                }

                break;
        }

        var note = new Entry { Placeholder = Strings.Get("Edit.Note"), FontFamily = "FigtreeRegular", FontSize = 16 };
        note.SetDynamicResource(Entry.TextColorProperty, "Ink");
        note.SetBinding(Entry.TextProperty, nameof(EditViewModel.Note));
        form.Add(Field(Strings.Get("Edit.Note"), note));

        var warnings = Ui.Text("", "Meta", look.Ink);
        warnings.SetBinding(Label.TextProperty, nameof(EditViewModel.Warnings));
        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(EditViewModel.Error));
        form.Add(warnings);
        form.Add(error);

        var save = new Button { Text = Strings.Get("Action.Save"), Command = _model.SaveCommand };
        save.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        form.Add(save);
        if (!_model.IsNew)
            form.Add(DeleteButton());
        _body.Content = Wrap(form);
    }

    private Button DeleteButton()
    {
        var delete = new Button { Text = Strings.Get("Edit.Delete"), Command = _model.DeleteCommand };
        delete.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        return delete;
    }

    private static View Wrap(View form) => new ContentView { Padding = new Thickness(24, 32, 24, 32), Content = form };

    private static VerticalStackLayout Field(string label, View input) =>
        new() { Spacing = 4, Children = { Ui.Text(label, "Label"), input } };

    private Grid DateAndTime(string dateProperty, string timeProperty)
    {
        var date = new DatePicker { MaximumDate = _model.Today.AddDays(1), FontFamily = "FigtreeRegular", FontSize = 16, Format = "ddd d MMM" };
        date.SetDynamicResource(DatePicker.TextColorProperty, "Ink");
        date.SetBinding(DatePicker.DateProperty, dateProperty);
        var clock = new TimePicker { FontFamily = "FigtreeRegular", FontSize = 16 };
        clock.SetDynamicResource(TimePicker.TextColorProperty, "Ink");
        clock.SetBinding(TimePicker.TimeProperty, timeProperty);
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        grid.Add(date, 0);
        grid.Add(clock, 1);
        return grid;
    }

    private static Entry Number(string property)
    {
        var entry = new Entry { Keyboard = Keyboard.Numeric, FontFamily = "FigtreeRegular", FontSize = 16 };
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetBinding(Entry.TextProperty, property);
        return entry;
    }

    private HorizontalStackLayout Choices((string Text, Action Choose, bool Selected)[] choices)
    {
        var row = new HorizontalStackLayout { Spacing = 8 };
        foreach (var (text, choose, selected) in choices)
        {
            var button = new Button { Text = text };
            button.SetDynamicResource(VisualElement.StyleProperty, selected ? "PrimaryButton" : "GhostButton");
            button.Clicked += (_, _) =>
            {
                choose();
                Render();
            };
            row.Add(button);
        }

        return row;
    }

    private static string Minutes(string sideKey) => $"{Strings.Get(sideKey)} ({Strings.Format("Edit.Minutes", "").Trim()})";

    private string KindKey() => _model.KindCode switch
    {
        "feeding" when _model.IsBottle => "Feed.Bottle",
        "feeding" => "Kind.Feed",
        "diaper" => "Kind.Diaper",
        "sleep" => "Kind.Sleep",
        _ => "Kind.Pump",
    };
}
