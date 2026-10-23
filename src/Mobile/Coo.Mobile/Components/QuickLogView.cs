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
    private bool _queued;

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
        Content = _body;
        model.PropertyChanged += OnChanged;
        Render();
    }

    /// <summary>Stops listening to the view model: the sheet's content is replaced when it next opens.</summary>
    public void Detach() => _model.PropertyChanged -= OnChanged;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickLogViewModel.Text) || _queued)
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
        var listening = _model.Stage == QuickLogStage.Listening;
        _body.Add(Ui.Text(_model.Hint, "Body", "Ink2"));

        if (_model.CanListen)
        {
            // One big target for a tired thumb: tap to speak, tap again to stop.
            var mic = new Border
            {
                WidthRequest = 96,
                HeightRequest = 96,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 48 },
                HorizontalOptions = LayoutOptions.Center,
                Content = new Icon(IconGeometry.Mic, "OnAccent", 40) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            };
            mic.SetDynamicResource(BackgroundColorProperty, listening ? "AccentInk" : "Accent");
            mic.GestureRecognizers.Add(new TapGestureRecognizer { Command = listening ? _model.StopListeningCommand : _model.ListenCommand });
            SemanticProperties.SetDescription(mic, Strings.Get(listening ? "Quick.Stop" : "Quick.Title"));
            _body.Add(mic);
            if (listening)
                _body.Add(Ui.Text(Strings.Get("Quick.Listening"), "BodyStrong", "Accent").Tap(l => l.HorizontalOptions = LayoutOptions.Center));
        }

        _body.Add(_inputRow);

        AddMessage();

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

    private void AddMessage()
    {
        if (!string.IsNullOrEmpty(_model.Message))
            _body.Add(Ui.Text(_model.Message, "Body", "AccentInk"));
    }
}
