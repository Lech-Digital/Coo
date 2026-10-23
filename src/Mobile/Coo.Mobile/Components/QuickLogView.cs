using System.ComponentModel;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.QuickLog;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The "Tell Coo" sheet (ADR 0005): a big mic, a text box, notes waiting to be understood, then the review — every
/// proposal with a tick, what Coo understood and what to check — and Save. The text box is built once, so redrawing the
/// rest never takes the keyboard away.
/// </summary>
public sealed class QuickLogView : ContentView
{
    private readonly QuickLogViewModel _model;
    private readonly Entry _entry;

    /// <summary>The text box in its frame with Send, built once: a view still inside an old frame cannot join a new one.</summary>
    private readonly Grid _inputRow;
    private readonly VerticalStackLayout _body = new() { Spacing = 14 };

    /// <summary>
    /// The mic, its "Listening…" and the message under the text box, built once and only restyled between Input and
    /// Listening: redrawing would take the button away from under a finger holding it, and the hold would end.
    /// </summary>
    private readonly Grid _mic;
    private readonly Border _micFace;
    private readonly Label _listeningLabel;
    private readonly Label _message;
    private bool _queued;
    private bool _inputShown;
    private long _pressedAt;
    private bool _startedByPress;

    /// <summary>Held at least this long, letting go sends what was said; a shorter press is a tap and keeps listening.</summary>
    private const long HoldMilliseconds = 600;

    public QuickLogView(QuickLogViewModel model)
    {
        _model = model;
        _entry = new Entry { Placeholder = Strings.Get("Quick.TypeHere"), ReturnType = ReturnType.Send, FontSize = 16 };
        _entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        _entry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        _entry.SetBinding(Entry.TextProperty, new Binding(nameof(QuickLogViewModel.Text), BindingMode.TwoWay, source: model));
        _entry.Completed += (_, _) => model.SendCommand.Execute(null);
        var send = new Button { Text = Strings.Get("Quick.Send"), Command = model.SendCommand, VerticalOptions = LayoutOptions.Center };
        send.SetDynamicResource(StyleProperty, "GhostButton");
        _inputRow = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
        _inputRow.Add(Ui.Rounded(_entry, "Surface", 14, new Thickness(12, 2), "Line"), 0);
        _inputRow.Add(send, 1);

        _micFace = new Border
        {
            WidthRequest = 96,
            HeightRequest = 96,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 48 },
            Content = new Icon(IconGeometry.Mic, "OnAccent", 40) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            InputTransparent = true,
        };
        var press = new Button { WidthRequest = 96, HeightRequest = 96, CornerRadius = 48, BackgroundColor = Colors.Transparent, BorderWidth = 0 };
        press.Pressed += (_, _) => OnMicPressed();
        press.Released += (_, _) => OnMicReleased();
        _mic = new Grid { HorizontalOptions = LayoutOptions.Center, Children = { _micFace, press } };
        SemanticProperties.SetHint(press, Strings.Get("Quick.HoldHint"));
        _listeningLabel = Ui.Text(Strings.Get("Quick.Listening"), "BodyStrong", "Accent").Tap(l => l.HorizontalOptions = LayoutOptions.Center);
        _message = Ui.Text("", "Body", "AccentInk");

        Content = _body;
        model.PropertyChanged += OnChanged;
        Render();
    }

    /// <summary>Stops listening to the view model: the sheet's content is replaced when it next opens.</summary>
    public void Detach() => _model.PropertyChanged -= OnChanged;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickLogViewModel.Text))
            return;
        var stays = _inputShown
                    && (_model.Stage is QuickLogStage.Input or QuickLogStage.Listening)
                    && (e.PropertyName is nameof(QuickLogViewModel.Stage) or nameof(QuickLogViewModel.Message) or nameof(QuickLogViewModel.SpeechLanguage));
        if (stays)
        {
            Dispatcher.Dispatch(UpdateInput);
            return;
        }

        if (_queued)
            return;
        _queued = true;
        Dispatcher.Dispatch(() =>
        {
            _queued = false;
            Render();
        });
    }

    private void Render()
    {
        _body.Children.Clear();
        _body.Add(Ui.Text(Strings.Get("Quick.Title"), "Section"));
        _inputShown = _model.Stage is QuickLogStage.Input or QuickLogStage.Listening;

        switch (_model.Stage)
        {
            case QuickLogStage.Review:
                RenderReview();
                break;
            case QuickLogStage.Thinking:
                var spinner = new ActivityIndicator { IsRunning = true, HeightRequest = 48, WidthRequest = 48, HorizontalOptions = LayoutOptions.Center };
                spinner.SetDynamicResource(ActivityIndicator.ColorProperty, "Accent");
                _body.Add(spinner);
                _body.Add(Ui.Text(Strings.Get("Quick.Thinking"), "Body", "Ink2").Tap(l => l.HorizontalOptions = LayoutOptions.Center));
                break;
            default:
                RenderInput();
                break;
        }
    }

    private void RenderInput()
    {
        _body.Add(Ui.Text(_model.Hint, "Body", "Ink2"));

        if (_model.CanListen)
        {
            _body.Add(_mic);
            _body.Add(_listeningLabel);
            _body.Add(SpeakIn());
        }

        _body.Add(_inputRow);
        _body.Add(_message);
        UpdateInput();

        if (_model.PendingNotes.Count > 0)
        {
            _body.Add(Ui.Text(Strings.Get("Quick.PendingTitle"), "Label", "Ink2"));
            foreach (var note in _model.PendingNotes)
            {
                var review = new Button { Text = Strings.Get("Quick.ReviewNote"), Command = _model.ReviewNoteCommand, CommandParameter = note };
                review.SetDynamicResource(StyleProperty, "PrimaryButton");
                var drop = new Button { Text = Strings.Get("Quick.DropNote"), Command = _model.DropNoteCommand, CommandParameter = note };
                drop.SetDynamicResource(StyleProperty, "GhostButton");
                var line = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8 };
                line.Add(Ui.Text($"“{note.Text}”", "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
                line.Add(review, 1);
                line.Add(drop, 2);
                _body.Add(line);
            }
        }

        _body.Add(Ui.Text(Strings.Get("Quick.Privacy"), "Meta", "Ink2").Tap(l => l.FontSize = 11));
    }

    private void RenderReview()
    {
        var list = new VerticalStackLayout { Spacing = 6 };
        foreach (var row in _model.Rows)
        {
            var box = new CheckBox { IsEnabled = row.IsEnabled, VerticalOptions = LayoutOptions.Center };
            box.SetDynamicResource(CheckBox.ColorProperty, "Accent");
            box.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(QuickLogRow.IsSelected), BindingMode.TwoWay, source: row));

            var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            text.Add(Ui.Text(row.Title, "BodyStrong"));
            text.Add(Ui.Text(row.When, "Meta", "Ink2"));
            if (row.Note is { } note)
                text.Add(Ui.Text(note, "Meta", "AccentInk"));

            var line = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 6, Padding = new Thickness(4, 6) };
            line.Add(box, 0);
            line.Add(text, 1);
            // The whole row toggles, not only the small box.
            line.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => { if (row.IsEnabled) row.IsSelected = !row.IsSelected; }) });
            list.Add(Ui.Rounded(line, "Surface", 16, new Thickness(6, 2), "Line"));
        }

        _body.Add(Ui.Text($"“{_model.Text}”", "Meta", "Ink2"));
        _body.Add(list);
        if (!string.IsNullOrEmpty(_model.Unparsed))
            _body.Add(Ui.Text(_model.Unparsed, "Meta", "Ink2"));
        AddMessage();

        var save = new Button { Text = Strings.Get("Quick.Save"), Command = _model.SaveCommand };
        save.SetDynamicResource(StyleProperty, "PrimaryButton");
        var cancel = new Button { Text = Strings.Get("Quick.Cancel"), Command = _model.CancelCommand };
        cancel.SetDynamicResource(StyleProperty, "GhostButton");
        var buttons = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 10 };
        buttons.Add(cancel, 0);
        buttons.Add(save, 1);
        _body.Add(buttons);
    }

    /// <summary>
    /// "Speak in": Android hears the language it is told to, so a parent who talks Polish to an English app chooses Polish
    /// here once; the phone remembers it.
    /// </summary>
    private Grid SpeakIn()
    {
        var languages = QuickLogViewModel.SpeechLanguages;
        var picker = new Picker { FontSize = 15, FontFamily = "FigtreeRegular", Title = Strings.Get("Quick.SpeakIn") };
        foreach (var language in languages)
            picker.Items.Add(Strings.NativeName(language));
        picker.SelectedIndex = Math.Max(0, languages.ToList().IndexOf(_model.SpeechLanguage));
        picker.SetDynamicResource(Picker.TextColorProperty, "Ink");
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
                _model.SetSpeechLanguageCommand.Execute(languages[picker.SelectedIndex]);
        };
        var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 10, HorizontalOptions = LayoutOptions.Center };
        row.Add(Ui.Text(Strings.Get("Quick.SpeakIn"), "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        row.Add(Ui.Rounded(picker, "Surface", 12, new Thickness(10, 0), "Line"), 1);
        return row;
    }

    private void UpdateInput()
    {
        var listening = _model.Stage == QuickLogStage.Listening;
        _micFace.SetDynamicResource(BackgroundColorProperty, listening ? "AccentInk" : "Accent");
        SemanticProperties.SetDescription(_mic, Strings.Get(listening ? "Quick.Stop" : "Quick.Title"));
        _listeningLabel.Text = Strings.Get(listening ? "Quick.ListeningRelease" : "Quick.HoldHint");
        _listeningLabel.SetDynamicResource(Label.TextColorProperty, listening ? "Accent" : "Ink2");
        _message.Text = _model.Message;
        _message.IsVisible = !string.IsNullOrEmpty(_model.Message);
    }

    // Press: starts listening, or, while listening, finishes it (the tap that ends a tapped start).
    private void OnMicPressed()
    {
        if (_model.Stage == QuickLogStage.Listening)
        {
            _startedByPress = false;
            _model.FinishListeningCommand.Execute(null);
            return;
        }

        _pressedAt = Environment.TickCount64;
        _startedByPress = true;
        _model.ListenCommand.Execute(null);
    }

    // Let go after a hold: that was walkie-talkie, send it. After a quick tap: keep listening until the next tap or a pause.
    private void OnMicReleased()
    {
        if (_startedByPress && Environment.TickCount64 - _pressedAt >= HoldMilliseconds)
            _model.FinishListeningCommand.Execute(null);
        _startedByPress = false;
    }

    private void AddMessage()
    {
        if (!string.IsNullOrEmpty(_model.Message))
            _body.Add(Ui.Text(_model.Message, "Body", "AccentInk"));
    }
}
