using Coo.Domain.CareLog.Kinds.Development;
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
        model.Closed += async (_, _) => await Navigation.PopAsync();
        model.Reloaded += (_, _) => Render();
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
        
        var badge = Ui.Rounded(new Icon(look.Icon, look.Ink, 24).Tap(i => i.VerticalOptions = LayoutOptions.Center), look.Tint, 16, new Thickness(8));
        badge.WidthRequest = badge.HeightRequest = 40;

        var title = Ui.Text(Strings.Get(KindKey()), "Title", look.Ink);
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        header.Add(back, 0);
        header.Add(badge.Tap(b => b.VerticalOptions = LayoutOptions.Center), 1);
        header.Add(title.Tap(l => l.VerticalOptions = LayoutOptions.Center), 2);

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

        form.Add(Field(_model.KindCode is "sleep" or "pump" or "tummy" || (_model.KindCode == "feeding" && !_model.IsBottle) ? Strings.Get("Edit.Start") : Strings.Get("Edit.Time"),
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
            case "feeding" when _model.IsInParts:
                // A feed in parts (Spec 011d §5): its parts read-only; the start above moves them all.
                foreach (var line in _model.PartLines)
                    form.Add(Ui.Text(line, "Body", "Ink2"));
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
                // Wet or dirty: a dirty diaper is usually wet too, and nobody can tell, so "both" is no longer offered
                // (owner, 2026-11-03). An entry already saved as both keeps that choice.
                var contents = new List<(string, Action, bool)>
                {
                    (Strings.Get("Diaper.Wet"), () => _model.Contents = DiaperContents.Wet, _model.Contents == DiaperContents.Wet),
                    (Strings.Get("Diaper.Dirty"), () => _model.Contents = DiaperContents.Dirty, _model.Contents == DiaperContents.Dirty),
                };
                if (_model.Contents == DiaperContents.Mixed)
                    contents.Add((Strings.Get("Diaper.Mixed"), () => _model.Contents = DiaperContents.Mixed, true));
                form.Add(Choices([.. contents]));
                break;
            case "tummy":
                if (!_model.IsRunning)
                    form.Add(Field(Strings.Get("Edit.End"), DateAndTime(nameof(EditViewModel.EndDate), nameof(EditViewModel.EndTime))));
                break;
            case "solids":
                form.Add(Field(Strings.Get("Edit.Food"), Text(nameof(EditViewModel.HealthName), Strings.Get("Edit.FoodPlaceholder"))));
                form.Add(Field(Strings.Get("Edit.FoodAmount"), Text(nameof(EditViewModel.HealthDetail), Strings.Get("Edit.FoodAmountPlaceholder"))));
                form.Add(Field(Strings.Get("Edit.FoodReaction"), Wrapped(Choices(new (string, Action, bool)[]
                {
                    (Strings.Get("Solids.None"), () => _model.SolidsReaction = SolidsReaction.None, _model.SolidsReaction == SolidsReaction.None),
                    (Strings.Get("Solids.Liked"), () => _model.SolidsReaction = SolidsReaction.Liked, _model.SolidsReaction == SolidsReaction.Liked),
                    (Strings.Get("Solids.Disliked"), () => _model.SolidsReaction = SolidsReaction.Disliked, _model.SolidsReaction == SolidsReaction.Disliked),
                    (Strings.Get("Solids.Reaction"), () => _model.SolidsReaction = SolidsReaction.Reaction, _model.SolidsReaction == SolidsReaction.Reaction),
                }))));
                if (_model.SolidsReaction == SolidsReaction.Reaction)
                    form.Add(Ui.Text(Strings.Get("Edit.FoodReactionHint"), "Meta", "Ink2"));
                break;
            case "milestone":
                form.Add(Wrapped(Choices([
                    .. MilestoneDetails.Presets.Select(code => (Strings.Get("Milestone." + code), (Action)(() => _model.MilestoneCode = code), _model.MilestoneCode == code)),
                    (Strings.Get("Milestone.custom"), () => _model.MilestoneCode = MilestoneDetails.Custom, _model.MilestoneCode == MilestoneDetails.Custom),
                ])));
                if (_model.MilestoneCode == MilestoneDetails.Custom)
                    form.Add(Field(Strings.Get("Edit.MilestoneTitle"), Text(nameof(EditViewModel.HealthName), Strings.Get("Edit.MilestonePlaceholder"))));
                form.Add(Ui.Text(Strings.Get("Edit.MilestoneHint"), "Meta", "Ink2"));
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
            case "growth":
                form.Add(Ui.Text(Strings.Get("Edit.GrowthHint"), "Meta", "Ink2"));
                form.Add(Field($"{Strings.Get("Edit.Weight")} ({_model.WeightUnit})", Number(nameof(EditViewModel.Weight))));
                form.Add(Field($"{Strings.Get("Edit.Length")} ({_model.LengthUnit})", Number(nameof(EditViewModel.Length))));
                form.Add(Field($"{Strings.Get("Edit.Head")} ({_model.LengthUnit})", Number(nameof(EditViewModel.Head))));
                break;
            case "medicine":
                form.Add(Field(Strings.Get("Edit.MedicineName"), Text(nameof(EditViewModel.HealthName), Strings.Get("Edit.MedicinePlaceholder"))));
                form.Add(Field(Strings.Get("Edit.Dose"), Text(nameof(EditViewModel.HealthDetail), "")));
                break;
            case "temperature":
                form.Add(Field($"{Strings.Get("Edit.Temperature")} ({_model.TemperatureUnit})", Number(nameof(EditViewModel.Temperature))));
                break;
            case "vaccine":
                form.Add(Field(Strings.Get("Edit.VaccineName"), Text(nameof(EditViewModel.HealthName), "")));
                break;
            case "visit":
                form.Add(Field(Strings.Get("Edit.VisitWith"), Text(nameof(EditViewModel.HealthDetail), Strings.Get("Edit.VisitPlaceholder"))));
                form.Add(Ui.Text(Strings.Get("Edit.VisitNoteHint"), "Meta", "Ink2"));
                break;
        }

        var note = new Entry { Placeholder = Strings.Get("Edit.Note"), FontFamily = "FigtreeRegular", FontSize = 16 };
        note.SetDynamicResource(Entry.TextColorProperty, "Ink");
        note.SetBinding(Entry.TextProperty, nameof(EditViewModel.Note));
        form.Add(Field(Strings.Get("Edit.Note"), Ui.Rounded(note, "Surface", 12, new Thickness(16, 4), "Line")));

        var warnings = Ui.Text("", "Meta", look.Ink);
        warnings.SetBinding(Label.TextProperty, nameof(EditViewModel.Warnings));
        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(EditViewModel.Error));
        form.Add(warnings);
        form.Add(error);

        var save = new Button
        {
            Text = Strings.Get("Action.Save"),
            Command = _model.SaveCommand,
            Margin = new Thickness(0, 16, 0, 0),
        };
        save.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        form.Add(save);
        if (!_model.IsNew)
            form.Add(DeleteButton());
        _body.Content = Wrap(form);
    }

    private Button DeleteButton()
    {
        var delete = new Button
        {
            Text = Strings.Get("Edit.Delete"),
            Command = _model.DeleteCommand,
            Margin = new Thickness(0, 6, 0, 0),
        };
        delete.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        delete.SetDynamicResource(Button.TextColorProperty, "FeedInk");
        return delete;
    }

    private static View Wrap(View form) => new ContentView { Padding = new Thickness(24, 32, 24, 32), Content = form };

    private static VerticalStackLayout Field(string label, View input) =>
        new() { Spacing = 4, Children = { Ui.Text(label, "Label"), input } };

    private View DateAndTime(string dateProperty, string timeProperty)
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
        return Ui.Rounded(grid, "Surface", 12, new Thickness(16, 4), "Line");
    }

    private static View Number(string property)
    {
        var entry = new Entry { Keyboard = Keyboard.Numeric, FontFamily = "FigtreeRegular", FontSize = 16 };
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetBinding(Entry.TextProperty, property);
        return Ui.Rounded(entry, "Surface", 12, new Thickness(16, 4), "Line");
    }

    private static View Text(string property, string placeholder)
    {
        var entry = new Entry { Placeholder = placeholder, FontFamily = "FigtreeRegular", FontSize = 16, MaxLength = 60 };
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        entry.SetBinding(Entry.TextProperty, property);
        return Ui.Rounded(entry, "Surface", 12, new Thickness(16, 4), "Line");
    }

    private HorizontalStackLayout Choices((string Text, Action Choose, bool Selected)[] choices)
    {
        var row = new HorizontalStackLayout { Spacing = 8 };
        foreach (var (text, choose, selected) in choices)
        {
            var button = new Button
            {
                Text = text,
                HeightRequest = 42,
                Padding = new Thickness(18, 0),
                CornerRadius = 21,
                FontFamily = "FigtreeSemiBold",
                FontSize = 14,
            };
            if (selected)
            {
                button.SetDynamicResource(VisualElement.BackgroundColorProperty, "Accent");
                button.SetDynamicResource(Button.TextColorProperty, "OnAccent");
                button.BorderWidth = 0;
            }
            else
            {
                button.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
                button.SetDynamicResource(Button.TextColorProperty, "Ink");
                button.SetDynamicResource(Button.BorderColorProperty, "Line");
                button.BorderWidth = 1;
            }
            button.Clicked += (_, _) =>
            {
                choose();
                Render();
            };
            row.Add(button);
        }

        return row;
    }

    /// <summary>Chips that wrap onto more lines: the reactions, and the eleven milestones.</summary>
    private static FlexLayout Wrapped(HorizontalStackLayout row)
    {
        var flex = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        foreach (var chip in row.Children.OfType<View>().ToList())
        {
            row.Remove(chip);
            chip.Margin = new Thickness(0, 0, 8, 8);
            flex.Add(chip);
        }

        return flex;
    }

    private static string Minutes(string sideKey) => $"{Strings.Get(sideKey)} ({Strings.Format("Edit.Minutes", "").Trim()})";

    private string KindKey() => _model.KindCode switch
    {
        "feeding" when _model.IsBottle => "Feed.Bottle",
        "feeding" => "Kind.Feed",
        "diaper" => "Kind.Diaper",
        "sleep" => "Kind.Sleep",
        "growth" => "Kind.Growth",
        "medicine" => "Kind.Medicine",
        "temperature" => "Kind.Temperature",
        "vaccine" => "Kind.Vaccine",
        "visit" => "Kind.Visit",
        "bath" => "Kind.Bath",
        "tummy" => "Kind.Tummy",
        "solids" => "Kind.Solids",
        "milestone" => "Kind.Milestone",
        _ => "Kind.Pump",
    };
}
