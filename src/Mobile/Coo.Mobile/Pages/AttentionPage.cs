using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Needs attention (Spec 007 §6): probable duplicates, unreadable entries, entries from a newer app, changes the server did not accept.</summary>
public sealed class AttentionPage : ContentPage
{
    private readonly AttentionViewModel _model;
    private readonly UndoService _undo;
    private readonly IServiceProvider _services;
    private bool _opening;
    private readonly ContentView _body = new();

    public AttentionPage(AttentionViewModel model, UndoService undo, IServiceProvider services)
    {
        _model = model;
        _services = services;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AttentionViewModel.FeedGroups) or nameof(AttentionViewModel.OtherGroups) or nameof(AttentionViewModel.Rejected))
                Dispatcher.Dispatch(Render);
        };
        _undo = undo;
        Shell.SetNavBarIsVisible(this, false);
        var root = new Grid();
        root.Add(new Backdrop());
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        Content = root;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _undo.DataChanged -= OnDataChanged;
        _model.EditRequested -= OnEditRequested;
    }

    private void OnEditRequested(object? sender, Coo.Domain.Common.EntryId id) => Dispatcher.Dispatch(async () =>
    {
        if (_opening)
            return;
        _opening = true;
#pragma warning disable CA1031 // Fire-and-forget: an escaped exception would crash the app.
        try
        {
            var page = _services.GetRequiredService<EditPage>();
            await page.OpenAsync(id);
            await Navigation.PushAsync(page);
        }
        catch (Exception)
        {
            // The page stays where it is; the entry is still listed.
        }
        finally
        {
            _opening = false;
        }
#pragma warning restore CA1031
    });

    // After an undo, or a delete that the window commits, the page shows what is now stored.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await SafeRefreshAsync());

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
        _model.EditRequested += OnEditRequested;
        await SafeRefreshAsync();
    }

#pragma warning disable CA1031 // Fire-and-forget: an escaped exception would crash the app.
    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception)
        {
            // The page keeps what it showed; the next change refreshes it.
        }
    }
#pragma warning restore CA1031

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
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        header.Add(back, 0);
        header.Add(Ui.Text(Strings.Get("Attention.Title"), "Title").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
        if (_model.CanResolveAll)
        {
            var resolveBtn = Small(Strings.Get("Attention.ResolveAll"), _model.AutoResolveAllCommand, primary: true);
            resolveBtn.VerticalOptions = LayoutOptions.Center;
            header.Add(resolveBtn, 2);
        }

        var stack = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(header);
        if (_model.IsEmpty)
            stack.Add(Ui.Text(Strings.Get("Attention.Empty"), "Body", "Ink2"));

        foreach (var (title, groups) in new[] { (Strings.Get("Attention.FeedsClose"), _model.FeedGroups), (Strings.Get("Attention.Duplicates"), _model.OtherGroups) })
        {
            if (groups.Count == 0)
                continue;
            stack.Add(Ui.Text(title, "Section"));
            foreach (var group in groups)
                stack.Add(GroupCard(group));
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

        if (_model.Rejected.Count > 0)
        {
            stack.Add(Ui.Text(Strings.Get("Attention.Rejected"), "Section"));
            foreach (var item in _model.Rejected)
            {
                stack.Add(new Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Text(item.Text, "BodyStrong"),
                        Ui.Text(item.Reason, "Meta"),
                        Small(Strings.Get("Edit.Title"), _model.EditRejectedCommand, primary: true, parameter: item.Id),
                        Small(Strings.Get("Action.Dismiss"), _model.DismissRejectedCommand, parameter: item.Id),
                        Small(Strings.Get("Edit.Delete"), _model.DeleteRejectedCommand, parameter: item.Id),
                    },
                }));
            }

            if (_model.HasMoreRejected)
                stack.Add(Small(Strings.Get("Attention.ShowMore"), _model.ShowMoreRejectedCommand));
        }

        _body.Content = stack;
    }

    /// <summary>
    /// A group card (Spec 011d §4): each entry with "Keep only this one", then why the feeds cannot be merged (when
    /// worth saying), "Merge into one feed" when they can, and "Keep all".
    /// </summary>
    private static Card GroupCard(GroupItem group)
    {
        var card = new VerticalStackLayout { Spacing = 10 };
        foreach (var member in group.Members)
        {
            card.Add(Ui.Text(member.Text, "BodyStrong"));
            card.Add(Small(Strings.Get("Attention.KeepOnly"), member.KeepOnly));
        }

        if (group.Why is { } why)
            card.Add(Ui.Text(why, "Meta"));
        if (group.Merge is { } merge)
            card.Add(Small(Strings.Get("Attention.Merge"), merge, primary: true));
        card.Add(Small(Strings.Get("Attention.KeepAll"), group.KeepAll, primary: group.Merge is null));
        return new Card(card);
    }

    private static Button Small(string text, System.Windows.Input.ICommand command, bool primary = false, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter, HorizontalOptions = LayoutOptions.Start };
        button.SetDynamicResource(VisualElement.StyleProperty, primary ? "PrimaryButton" : "GhostButton");
        return button;
    }
}
