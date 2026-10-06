using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Setup;

namespace Coo.Mobile.Pages;

/// <summary>First launch (Spec 006 §3, §3a): has the baby arrived, the name, and the dates.</summary>
public sealed class SetupPage : ContentPage
{
    public SetupPage(SetupViewModel model)
    {
        BindingContext = model;
        var yes = Choice(Strings.Get("Setup.Yes"), () => model.HasArrived = true);
        var notYet = Choice(Strings.Get("Setup.NotYet"), () => model.HasArrived = false);

        var name = new Entry { Placeholder = Strings.Get("Setup.Name"), FontFamily = "FigtreeRegular", FontSize = 17 };
        name.SetBinding(Entry.TextProperty, nameof(SetupViewModel.Name));
        name.SetDynamicResource(Entry.TextColorProperty, "Ink");

        var birth = DatePicker(nameof(SetupViewModel.BirthDate), max: model.Today);
        var bornEarly = new HorizontalStackLayout { Spacing = 8 };
        var early = new CheckBox();
        early.SetBinding(CheckBox.IsCheckedProperty, nameof(SetupViewModel.BornEarly));
        early.SetDynamicResource(CheckBox.ColorProperty, "Accent");
        bornEarly.Add(early);
        bornEarly.Add(Ui.Text(Strings.Get("Setup.BornEarly"), "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center));

        var due = DatePicker(nameof(SetupViewModel.DueDate), max: model.Today.AddDays(308));
        var dueLabel = Ui.Text(Strings.Get("Setup.DueDate"), "Label");
        var birthLabel = Ui.Text(Strings.Get("Setup.BirthDate"), "Label");
        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(SetupViewModel.Error));

        var go = new Button { Text = Strings.Get("Setup.Continue"), Command = model.ContinueCommand, Margin = new Thickness(0, 16, 0, 0) };
        go.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");

        // Spec 011b §4.4: "Have an invitation? Enter a code".
        var code = Ui.Text(SetupViewModel.HaveInvitationText, "BodyStrong", "Accent").Tap(l => l.Margin = new Thickness(0, 8, 0, 0));
        code.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.EnterCodeCommand });

        void Sync()
        {
            yes.SetDynamicResource(VisualElement.StyleProperty, model.HasArrived ? "PrimaryButton" : "GhostButton");
            notYet.SetDynamicResource(VisualElement.StyleProperty, model.HasArrived ? "GhostButton" : "PrimaryButton");
            birthLabel.IsVisible = birth.IsVisible = bornEarly.IsVisible = model.HasArrived;
            dueLabel.IsVisible = due.IsVisible = !model.HasArrived || model.BornEarly;
        }

        model.PropertyChanged += (_, _) => Sync();
        Sync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 64, 24, 32),
                Spacing = 14,
                Children =
                {
                    Ui.Text(Strings.Get("Setup.Title"), "Title"),
                    Ui.Text(Strings.Get("Setup.Hint"), "Body", "Ink2"),
                    Ui.Text(Strings.Get("Setup.Arrived"), "Section").Tap(l => l.Margin = new Thickness(0, 16, 0, 0)),
                    new HorizontalStackLayout { Spacing = 8, Children = { yes, notYet } },
                    Ui.Text(Strings.Get("Setup.Name"), "Label"),
                    name,
                    birthLabel,
                    birth,
                    bornEarly,
                    dueLabel,
                    due,
                    error,
                    go,
                    code,
                },
            },
        };
    }

    private static Button Choice(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Clicked += (_, _) => action();
        return button;
    }

    private static DatePicker DatePicker(string property, DateTime max)
    {
        var picker = new DatePicker { MaximumDate = max, FontFamily = "FigtreeRegular", FontSize = 17, Format = "D" };
        picker.SetBinding(Microsoft.Maui.Controls.DatePicker.DateProperty, property);
        picker.SetDynamicResource(Microsoft.Maui.Controls.DatePicker.TextColorProperty, "Ink");
        return picker;
    }
}
