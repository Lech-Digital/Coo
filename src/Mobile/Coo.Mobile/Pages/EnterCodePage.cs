using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// "Join a family" · "Enter the code from your invitation." (spec §4.4): capitals, no autocorrect; Continue opens the flow.
/// It needs no account (the join flow signs in), so it stays open across a sign-out. Every handler catches everything.
/// </summary>
public sealed class EnterCodePage : ContentPage
{
    public EnterCodePage(EnterCodeViewModel model)
    {
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        var field = new Entry
        {
            Placeholder = "ABCD-EFGH",
            Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeCharacter),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FontFamily = "FigtreeRegular",
            FontSize = 20,
        };
        field.SetDynamicResource(Entry.TextColorProperty, "Ink");
        field.TextChanged += (_, e) => model.Code = e.NewTextValue ?? "";
        var error = Ui.Text("", "Meta", "FeedInk");
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnterCodeViewModel.Error))
                Dispatcher.Dispatch(() => error.Text = model.Error);
        };
        var go = new Button { Text = EnterCodeViewModel.ContinueText };
        go.SetDynamicResource(StyleProperty, "PrimaryButton");
        go.Clicked += async (_, _) =>
        {
            try
            {
                await model.ContinueCommand.ExecuteAsync(null);
            }
#pragma warning disable CA1031 // async void: nothing may escape; a failed local write leaves the sheet open.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        };
        model.Accepted += (_, _) => Dispatcher.Dispatch(async () => await AcceptedAsync());
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(24, 48, 24, 32),
                Children = { Ui.Text(EnterCodeViewModel.Title, "Title"), Ui.Text(EnterCodeViewModel.Hint, "Body"), field, error, go },
            },
        };
    }

    /// <summary>Raised once the code is stored and the sheet closed (main thread); the prompter then opens the join sheet.</summary>
    public event EventHandler? CodeAccepted;

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    /// <summary>Dispatched from <see cref="EnterCodeViewModel.Accepted"/>: nothing may escape, the listeners included.</summary>
    private async Task AcceptedAsync()
    {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape; the code is stored, so the flow opens at the next resume.
        try
        {
            await CloseAsync();
            CodeAccepted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
