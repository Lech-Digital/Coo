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

        var nameEntry = new Entry { Placeholder = Strings.Get("Setup.Name"), FontFamily = "FigtreeRegular", FontSize = 17 };
        nameEntry.SetBinding(Entry.TextProperty, nameof(SetupViewModel.Name));
        nameEntry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        nameEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        var name = Ui.Rounded(nameEntry, "Surface", 12, new Thickness(16, 4), "Line");

        // Girl or boy, once, with the baby: the growth chart's standard, shared with the family. Optional.
        var girl = Choice(Strings.Get("Baby.Girl"), () => model.ChooseSexCommand.Execute(Coo.Domain.Households.BabySex.Girl));
        var boy = Choice(Strings.Get("Baby.Boy"), () => model.ChooseSexCommand.Execute(Coo.Domain.Households.BabySex.Boy));
        var sexHint = Ui.Text(Strings.Get("Baby.SexHint"), "Meta", "Ink2");

        var birthPicker = DatePicker(nameof(SetupViewModel.BirthDate), max: model.Today);
        var birth = Ui.Rounded(birthPicker, "Surface", 12, new Thickness(16, 4), "Line");
        var bornEarly = new HorizontalStackLayout { Spacing = 8 };
        var early = new CheckBox();
        early.SetBinding(CheckBox.IsCheckedProperty, nameof(SetupViewModel.BornEarly));
        early.SetDynamicResource(CheckBox.ColorProperty, "Accent");
        bornEarly.Add(early);
        bornEarly.Add(Ui.Text(Strings.Get("Setup.BornEarly"), "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center));

        var duePicker = DatePicker(nameof(SetupViewModel.DueDate), max: model.Today.AddDays(308));
        var due = Ui.Rounded(duePicker, "Surface", 12, new Thickness(16, 4), "Line");
        var dueLabel = Ui.Text(Strings.Get("Setup.DueDate"), "Label");
        var birthLabel = Ui.Text(Strings.Get("Setup.BirthDate"), "Label");
        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(SetupViewModel.Error));

        var go = new Button { Text = Strings.Get("Setup.Continue"), Command = model.ContinueCommand, Margin = new Thickness(0, 16, 0, 0) };
        go.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");

        // Spec 011b §4.4: "Have an invitation? Enter a code".
        var code = Ui.Text(SetupViewModel.HaveInvitationText, "BodyStrong", "Accent").Tap(l => l.Margin = new Thickness(0, 8, 0, 0));
        code.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.EnterCodeCommand });

        // Someone with a family already (a new phone, a reinstall): sign in and the family's log comes, no second baby.
        var signIn = Ui.Text(SetupViewModel.SignInText, "BodyStrong", "Accent").Tap(l => l.Margin = new Thickness(0, 4, 0, 0));
        signIn.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.SignInCommand });
        var status = Ui.Text("", "Body", "Ink2");
        status.SetBinding(Label.TextProperty, nameof(SetupViewModel.Status));

        // Signed in to an account with a family: no baby form, just the family's log on its way (owner's finding).
        var spinner = new ActivityIndicator { IsRunning = true, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(0, 16, 0, 0) };
        spinner.SetDynamicResource(ActivityIndicator.ColorProperty, "Accent");
        var instead = Ui.Text(SetupViewModel.AddBabyInsteadText, "BodyStrong", "Accent").Tap(l => l.Margin = new Thickness(0, 24, 0, 0));
        instead.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.AddBabyInsteadCommand });

        var hint = Ui.Text(model.Hint, "Body", "Ink2");
        var form = new VerticalStackLayout
        {
            Spacing = 14,
            Children =
            {
                Ui.Text(Strings.Get("Setup.Arrived"), "Section").Tap(l => l.Margin = new Thickness(0, 16, 0, 0)),
                new HorizontalStackLayout { Spacing = 8, Children = { yes, notYet } },
                Ui.Text(Strings.Get("Setup.Name"), "Label"),
                name,
                Ui.Text(Strings.Get("Baby.Sex"), "Label"),
                new HorizontalStackLayout { Spacing = 8, Children = { girl, boy } },
                sexHint,
                birthLabel,
                birth,
                bornEarly,
                dueLabel,
                due,
                error,
                go,
                code,
            },
        };

        void Sync()
        {
            yes.SetDynamicResource(VisualElement.StyleProperty, model.HasArrived ? "PrimaryButton" : "GhostButton");
            notYet.SetDynamicResource(VisualElement.StyleProperty, model.HasArrived ? "GhostButton" : "PrimaryButton");
            girl.SetDynamicResource(VisualElement.StyleProperty, model.Sex == Coo.Domain.Households.BabySex.Girl ? "PrimaryButton" : "GhostButton");
            boy.SetDynamicResource(VisualElement.StyleProperty, model.Sex == Coo.Domain.Households.BabySex.Boy ? "PrimaryButton" : "GhostButton");
            birthLabel.IsVisible = birth.IsVisible = bornEarly.IsVisible = model.HasArrived;
            dueLabel.IsVisible = due.IsVisible = !model.HasArrived || model.BornEarly;
            signIn.IsVisible = model.CanSignIn && !model.IsFetching;
            status.IsVisible = model.Status.Length > 0;
            spinner.IsVisible = instead.IsVisible = model.IsFetching;
            form.IsVisible = hint.IsVisible = !model.IsFetching;
        }

        model.PropertyChanged += (_, _) => Sync();
        Sync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 64, 24, 32),
                Spacing = 14,
                Children = { Ui.Text(Strings.Get("Setup.Title"), "Title"), hint, form, signIn, status, spinner, instead },
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
