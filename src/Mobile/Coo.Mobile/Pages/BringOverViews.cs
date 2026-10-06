using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>Spec 011b §6's choice, mapping and old-family question, drawn from <see cref="FamilyBringOverViewModel"/>.</summary>
internal static class BringOverViews
{
    /// <param name="model">The bring-over flow.</param>
    /// <param name="run">The page's runner, which says when an action failed.</param>
    public static View Render(FamilyBringOverViewModel model, Func<Func<Task>, Task> run)
    {
        var stack = new VerticalStackLayout { Spacing = 16 };
        switch (model.Step)
        {
            case BringOverStep.Choice:
                stack.Add(Ui.Text(model.Summary, "Title"));
                stack.Add(Ui.Text(model.FamilyLine, "Body"));
                stack.Add(Button(FamilyBringOverViewModel.BringText, "PrimaryButton", () => run(() => model.BringCommand.ExecuteAsync(null))));
                if (model.CanKeep)
                    stack.Add(Button(FamilyBringOverViewModel.KeepText, "GhostButton", () => run(() => model.KeepCommand.ExecuteAsync(null))));
                break;
            case BringOverStep.Mapping:
                stack.Add(Ui.Text(model.Summary, "Title"));
                foreach (var mapping in model.Mappings)
                {
                    var picker = new Picker { ItemsSource = mapping.Options.Select(o => o.Label).ToList(), SelectedIndex = mapping.Selected };
                    picker.SelectedIndexChanged += (_, _) => mapping.Selected = picker.SelectedIndex;
                    stack.Add(Ui.Text(mapping.Prompt, "BodyStrong"));
                    stack.Add(picker);
                }

                stack.Add(Button(FamilyBringOverViewModel.ConfirmText, "PrimaryButton", () => run(() => model.ConfirmCommand.ExecuteAsync(null))));
                break;
            case BringOverStep.OldFamily:
                stack.Add(Ui.Text(model.Question, "Body"));
                if (model.QuestionNote.Length > 0)
                    stack.Add(Ui.Text(model.QuestionNote, "Body"));
                stack.Add(Button(model.AnswerText, "PrimaryButton", () => run(() => model.AnswerCommand.ExecuteAsync(null))));
                stack.Add(Button(FamilyBringOverViewModel.KeepOldText, "GhostButton", () =>
                {
                    model.KeepOldCommand.Execute(null);
                    return Task.CompletedTask;
                }));
                break;
        }

        if (model.Toast.Length > 0)
            stack.Add(Ui.Text(model.Toast, "Meta"));
        return stack;
    }

    private static Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(VisualElement.StyleProperty, style);
        button.Clicked += async (_, _) =>
        {
            // Here, not in each caller: no button of this view can crash the app, whatever runner it was given.
            try
            {
                await action();
            }
#pragma warning disable CA1031 // async void handler: nothing may escape; the runner and the view model show failures.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        };
        return button;
    }
}
