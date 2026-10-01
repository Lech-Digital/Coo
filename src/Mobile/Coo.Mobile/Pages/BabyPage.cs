using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>The baby (Spec 009 §3.1): name, nicknames and dates, one Save. A pushed page, so the keyboard and Back work.</summary>
public sealed class BabyPage : ContentPage
{
    private readonly BabyProfileViewModel _model;

    public BabyPage(BabyProfileViewModel model)
    {
        _model = model;
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.Container, SafeAreaRegions.SoftInput);
        model.Saved += async (_, _) => await Navigation.PopAsync();
    }

    /// <summary>Back returns to Settings (Shell does not pop pushed pages on Back; found in Increment 7).</summary>
    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.LoadAsync();
        Content = new ScrollView { Content = Build() };
    }

    private VerticalStackLayout Build()
    {
        var name = TextEntry(nameof(BabyProfileViewModel.Name), Strings.Get("Setup.Name"));
        var nicknames = TextEntry(nameof(BabyProfileViewModel.Nicknames), Strings.Get("Baby.NicknamesHint"));

        var birthLabel = Ui.Text(Strings.Get("Setup.BirthDate"), "Label");
        var birth = DatePicker(nameof(BabyProfileViewModel.BirthDate), max: _model.Today);
        var early = new CheckBox();
        early.SetBinding(CheckBox.IsCheckedProperty, nameof(BabyProfileViewModel.HasDueDate));
        early.SetDynamicResource(CheckBox.ColorProperty, "Accent");
        var bornEarly = new HorizontalStackLayout { Spacing = 8, Children = { early, Ui.Text(Strings.Get("Setup.BornEarly"), "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center) } };
        var dueLabel = Ui.Text(Strings.Get("Setup.DueDate"), "Label");
        var due = DatePicker(nameof(BabyProfileViewModel.DueDate), max: _model.Today.AddDays(308));

        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(BabyProfileViewModel.Error));
        var save = new Button { Text = Strings.Get("Action.Save"), Command = _model.SaveCommand, Margin = new Thickness(0, 16, 0, 0) };
        save.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");

        void Sync()
        {
            birthLabel.IsVisible = birth.IsVisible = bornEarly.IsVisible = _model.IsBorn;
            dueLabel.IsVisible = due.IsVisible = !_model.IsBorn || _model.HasDueDate;
        }

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BabyProfileViewModel.HasDueDate) or nameof(BabyProfileViewModel.IsBorn))
                Sync();
        };
        Sync();

        return new VerticalStackLayout
        {
            Padding = new Thickness(24, 32, 24, 32),
            Spacing = 14,
            Children =
            {
                Ui.BackHeader(Strings.Get("Settings.Baby"), () => Navigation.PopAsync()),
                Ui.Text(Strings.Get("Setup.Name"), "Label").Tap(l => l.Margin = new Thickness(0, 8, 0, 0)),
                name,
                Ui.Text(Strings.Get("Baby.Nicknames"), "Label"),
                nicknames,
                birthLabel,
                birth,
                bornEarly,
                dueLabel,
                due,
                error,
                save,
            },
        };
    }

    private static Entry TextEntry(string property, string placeholder)
    {
        var entry = new Entry { Placeholder = placeholder, FontFamily = "FigtreeRegular", FontSize = 17 };
        entry.SetBinding(Entry.TextProperty, property);
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        return entry;
    }

    private static DatePicker DatePicker(string property, DateTime max)
    {
        var picker = new DatePicker { MaximumDate = max, FontFamily = "FigtreeRegular", FontSize = 17, Format = "D" };
        picker.SetBinding(Microsoft.Maui.Controls.DatePicker.DateProperty, property);
        picker.SetDynamicResource(Microsoft.Maui.Controls.DatePicker.TextColorProperty, "Ink");
        return picker;
    }
}
