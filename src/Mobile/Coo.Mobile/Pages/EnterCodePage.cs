using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// "Join a family" · "Enter the code from your invitation." (spec §4.4): capitals, no autocorrect; Continue opens the flow.
/// It needs no account (the join flow signs in), so it stays open across a sign-out. Every handler catches everything.
/// </summary>
public sealed class EnterCodePage : ContentPage
{
    private bool _closeWanted;

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
                Dispatcher.Dispatch(() =>
                {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
                    try
                    {
                        error.Text = model.Error;
                    }
                    catch (Exception)
                    {
                    }
#pragma warning restore CA1031
                });
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

    /// <summary>A close asked for while another sheet was over this one (M1): now it is on top.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_closeWanted)
            Dispatcher.Dispatch(async () => await CloseAsync());
    }

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

    /// <summary>Pops this page only when it is on top (M1); with a sheet over it, the close waits until this page appears again.</summary>
    private async Task CloseAsync()
    {
        try
        {
            var stack = Navigation.ModalStack;
            if (stack.Count > 0 && ReferenceEquals(stack[stack.Count - 1], this))
            {
                _closeWanted = false;
                await Navigation.PopModalAsync();
            }
            else if (stack.Contains(this))
            {
                _closeWanted = true;
            }
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
