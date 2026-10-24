using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Components;

/// <summary>
/// Coo's own dialogs in place of Android's grey ones (owner, 2026-10-24: "brand them nicely"): a rounded Washi card over
/// a dimmed page, day and night, with Coo's buttons. A transparent modal page, so it sits above sheets and tabs; Back
/// and a tap outside answer like Cancel. Android's own permission prompts cannot be styled: <see cref="PrimeAsync"/>
/// explains in Coo's words first, and the system prompt that follows is short.
/// </summary>
public static class CooDialog
{
    /// <summary>A question with two answers; true for <paramref name="accept"/>.</summary>
    public static async Task<bool> ConfirmAsync(Page page, string title, string message, string accept, string cancel) =>
        await ShowAsync(page, title, message, [accept], cancel, primaryFirst: true) == 0;

    /// <summary>Something to read, and OK.</summary>
    public static Task AlertAsync(Page page, string title, string message, string? ok = null) =>
        ShowAsync(page, title, message, [ok ?? Strings.Get("Join.OK")], cancel: null, primaryFirst: true);

    /// <summary>One of several choices; the chosen one's position, or -1 for Cancel.</summary>
    public static Task<int> ChooseAsync(Page page, string title, IReadOnlyList<string> options, string? cancel = null) =>
        ShowAsync(page, title, null, options, cancel ?? Strings.Get("Action.Cancel"), primaryFirst: false);

    /// <summary>
    /// Before Android asks for a permission: what it is for, in Coo's words. Already granted, nothing shows; "Not now"
    /// means the system prompt is not shown either.
    /// </summary>
    public static async Task<bool> PrimeAsync<TPermission>(Page page, string titleKey, string bodyKey)
        where TPermission : Permissions.BasePermission, new()
    {
        try
        {
            if (await Permissions.CheckStatusAsync<TPermission>() == PermissionStatus.Granted)
                return true;
        }
#pragma warning disable CA1031 // A permission Android does not know here: let the caller try.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }

        return await ConfirmAsync(page, Strings.Get(titleKey), Strings.Get(bodyKey), Strings.Get("Permission.Continue"), Strings.Get("Advisor.ConsentLater"));
    }

    private static async Task<int> ShowAsync(Page page, string title, string? message, IReadOnlyList<string> buttons, string? cancel, bool primaryFirst)
    {
        ArgumentNullException.ThrowIfNull(page);
        var answer = new TaskCompletionSource<int>();
        var dialog = new DialogPage(title, message, buttons, cancel, primaryFirst, answer);
        var modals = page.Navigation.ModalStack;
        var navigation = modals.Count > 0 ? modals[^1].Navigation : page.Navigation;
        await navigation.PushModalAsync(dialog, animated: false);
        var result = await answer.Task;
        await dialog.Navigation.PopModalAsync(animated: false);
        return result;
    }

    private sealed class DialogPage : ContentPage
    {
        private readonly TaskCompletionSource<int> _answer;

        public DialogPage(string title, string? message, IReadOnlyList<string> buttons, string? cancel, bool primaryFirst, TaskCompletionSource<int> answer)
        {
            _answer = answer;
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.45);
            Shell.SetNavBarIsVisible(this, false);

            var stack = new VerticalStackLayout { Spacing = 14 };
            stack.Add(Ui.Text(title, "Section"));
            if (!string.IsNullOrWhiteSpace(message))
                stack.Add(new ScrollView { MaximumHeightRequest = 420, Content = Ui.Text(message, "Body", "Ink2") });

            if (primaryFirst)
            {
                // A question or a notice: the answer as the accent button, Cancel beside it.
                var row = new Grid { ColumnSpacing = 10 };
                var answers = new List<View>();
                if (cancel is not null)
                    answers.Add(Button(cancel, "GhostButton", -1));
                for (var i = 0; i < buttons.Count; i++)
                    answers.Add(Button(buttons[i], "PrimaryButton", i));
                for (var i = 0; i < answers.Count; i++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    row.Add(answers[i], i);
                }

                stack.Add(row);
            }
            else
            {
                // A choice: each option a full-width row, then Cancel.
                for (var i = 0; i < buttons.Count; i++)
                    stack.Add(Option(buttons[i], i));
                if (cancel is not null)
                    stack.Add(Button(cancel, "GhostButton", -1));
            }

            var card = Ui.Rounded(stack, "Surface", 24, new Thickness(22, 22, 22, 18), "Line");
            card.Margin = new Thickness(24, 0);
            card.VerticalOptions = LayoutOptions.Center;
            card.MaximumWidthRequest = 460;

            // A tap on the dimmed area outside the card is Cancel (or OK for a notice).
            var outside = new Grid();
            outside.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Answer(cancel is null ? 0 : -1)) });
            card.GestureRecognizers.Add(new TapGestureRecognizer());
            outside.Add(card);
            Content = outside;
        }

        protected override bool OnBackButtonPressed()
        {
            Answer(-1);
            return true;
        }

        private void Answer(int value) => _answer.TrySetResult(value);

        private Button Button(string text, string style, int value)
        {
            var button = new Button { Text = text, LineBreakMode = LineBreakMode.WordWrap };
            button.SetDynamicResource(StyleProperty, style);
            button.Clicked += (_, _) => Answer(value);
            return button;
        }

        private View Option(string text, int value)
        {
            var label = Ui.Text(text, "Body");
            label.VerticalOptions = LayoutOptions.Center;
            var row = Ui.Rounded(label, "Ground", 14, new Thickness(16, 14), "Line");
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Answer(value)) });
            SemanticProperties.SetDescription(row, text);
            return row;
        }
    }
}
