using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Components;

/// <summary>
/// The one toast, with Undo when the action offers it (Spec 007 §5). Every page that writes shows it; the
/// <see cref="UndoService"/> decides what it says and how long it stays.
/// </summary>
public sealed class UndoToastView : ContentView
{
    private readonly UndoService _undo;
    private readonly Label _text = Ui.Text("", "BodyStrong", "VoiceInk");
    private readonly Button _button;
    private int _version;

    public UndoToastView(UndoService undo)
    {
        _undo = undo;
        _button = new Button { Text = Strings.Get("Action.Undo"), BackgroundColor = Colors.Transparent, Padding = new Thickness(12, 0), FontFamily = "FigtreeBold" };
        // VoiceInk, not Accent: the accent green had too little contrast on the dark toast (found on the phone).
        _button.SetDynamicResource(Button.TextColorProperty, "VoiceInk");
        _button.Clicked += async (_, _) => await _undo.UndoAsync();

        var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        _text.VerticalOptions = LayoutOptions.Center;
        row.Add(_text, 0);
        row.Add(_button, 1);
        Content = Ui.Rounded(row, "VoiceBg", 999, new Thickness(20, 6, 8, 6));
        Margin = new Thickness(24, 0, 24, 16);
        VerticalOptions = LayoutOptions.End;
        IsVisible = false;

        // Subscribed only while on screen: the service outlives pages, which are rebuilt on a language change.
        Loaded += (_, _) => _undo.ToastChanged += OnToastChanged;
        Unloaded += (_, _) => _undo.ToastChanged -= OnToastChanged;
    }

    private void OnToastChanged(object? sender, UndoToast? toast) => Dispatcher.Dispatch(() => Show(toast));

    private async void Show(UndoToast? toast)
    {
        var version = ++_version;
        if (toast is null)
        {
            IsVisible = false;
            return;
        }

        _text.Text = toast.Text;
        _button.IsVisible = toast.CanUndo;
        IsVisible = true;

        // A toast without Undo leaves after 3 seconds; with Undo, the service hides it when the window closes.
        if (!toast.CanUndo)
        {
            await Task.Delay(3000);
            if (version == _version)
                IsVisible = false;
        }
    }
}
