using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Needs attention (Spec 007 §6): probable duplicates, unreadable entries, entries from a newer app.</summary>
public sealed class AttentionPage : ContentPage
{
    private readonly AttentionViewModel _model;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();

    public AttentionPage(AttentionViewModel model, UndoService undo)
    {
        _model = model;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttentionViewModel.Duplicates))
                Dispatcher.Dispatch(Render);
        };
        _undo = undo;
        Shell.SetNavBarIsVisible(this, false);
        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        Content = root;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _undo.DataChanged -= OnDataChanged;
    }

    // After an undo, or a delete that the window commits, the page shows what is now stored.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await RefreshAsync());

    /// <summary>
    /// Back returns to the page below. Shell did not pop pushed pages on Back (found on the Pixel 10 in Increment 7):
    /// Coo went to the background instead.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _undo.DataChanged += OnDataChanged;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await _model.RefreshAsync();
        Render();
    }

    private void Render()
    {
        var back = Ui.Rounded(new Icon(IconGeometry.ChevronLeft, "Ink", 24).Tap(i => i.HorizontalOptions = LayoutOptions.Center), "Surface", 999, new Thickness(12), "Line");
        back.WidthRequest = back.HeightRequest = 48;
        back.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Navigation.PopAsync()) });
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        header.Add(back, 0);
        header.Add(Ui.Text(Strings.Get("Attention.Title"), "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);

        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(header);
        if (_model.IsEmpty)
            stack.Add(Ui.Text(Strings.Get("Attention.Empty"), "Body", "Ink2"));

        if (_model.Duplicates.Count > 0)
            stack.Add(Ui.Text(Strings.Get("Attention.Duplicates"), "Section"));
        foreach (var d in _model.Duplicates)
        {
            stack.Add(new Card(new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    Ui.Text(d.FirstText, "BodyStrong"),
                    Small(Strings.Get("Attention.KeepThis"), d.KeepFirst),
                    Ui.Text(d.SecondText, "BodyStrong"),
                    Small(Strings.Get("Attention.KeepThis"), d.KeepSecond),
                    Small(Strings.Get("Attention.KeepBoth"), d.KeepBoth, primary: true),
                },
            }));
        }

        foreach (var (title, items) in new[] { (Strings.Get("Attention.Unreadable"), _model.Unreadable), (Strings.Get("Attention.Newer"), _model.Newer) })
        {
            if (items.Count == 0)
                continue;
            stack.Add(Ui.Text(title, "Section"));
            foreach (var item in items)
            {
                stack.Add(new Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Text(item.Text, "BodyStrong"),
                        Ui.Text(item.Reason, "Meta"),
                        Small(Strings.Get("Edit.Delete"), _model.DeleteEntryCommand, parameter: item.Id),
                    },
                }));
            }
        }

        _body.Content = stack;
    }

    private static Button Small(string text, System.Windows.Input.ICommand command, bool primary = false, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter, HorizontalOptions = LayoutOptions.Start };
        button.SetDynamicResource(VisualElement.StyleProperty, primary ? "PrimaryButton" : "GhostButton");
        return button;
    }
}
