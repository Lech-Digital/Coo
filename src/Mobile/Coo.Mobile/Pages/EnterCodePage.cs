using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.Pages;

/// <summary>
/// "Join a family" · "Enter the code from your invitation." (spec §4.4): capitals, no autocorrect; Continue opens the flow.
/// It needs no account (the join flow signs in), so it stays open across a sign-out. Every handler catches everything.
/// </summary>
public sealed class EnterCodePage : ContentPage
{
    private readonly SheetNavigator _sheets;
    private bool _closeWanted;

    public EnterCodePage(EnterCodeViewModel model, SheetNavigator sheets)
    {
        _sheets = sheets;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        var fieldEntry = new Entry
        {
            Placeholder = "ABCD-EFGH",
            Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeCharacter),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FontFamily = "FigtreeRegular",
            FontSize = 20,
        };
        fieldEntry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        fieldEntry.TextChanged += (_, e) => model.Code = e.NewTextValue ?? "";
        var field = Ui.Rounded(fieldEntry, "Surface", 12, new Thickness(16, 4), "Line");
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
        Dispatcher.Dispatch(async () =>
        {
            var suggested = await model.SuggestedAsync();
            if (suggested.Length > 0 && string.IsNullOrEmpty(fieldEntry.Text))
                fieldEntry.Text = suggested;
        });
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(24, 48, 24, 32),
                Children = { Ui.BackHeader(EnterCodeViewModel.Title, CloseAsync), Ui.Text(EnterCodeViewModel.Hint, "Body"), field, error, go },
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

    /// <summary>Pops this page, never another one (walk fix F1); with a sheet over it, the close waits until this page appears again (M1).</summary>
    private async Task CloseAsync()
    {
        try
        {
            _closeWanted = await _sheets.CloseAsync(this) == SheetClose.Covered;
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
