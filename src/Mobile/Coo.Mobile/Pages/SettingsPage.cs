using Coo.Domain.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// The Settings tab (Spec 009 §2–§3, Spec 010 §2): account, baby, what you log, units, appearance, language, about.
/// One scrolling page; it is rebuilt from the model whenever the model changes, like Log.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _model;
    private readonly AccountViewModel _account;
    private readonly IServiceProvider _services;
    private readonly IClockFormat _clock;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private readonly LanguageController _language;
    private bool _confirming;
    private bool _rendering;
    private bool _renderQueued;
    private string? _renderedLanguage;

    public SettingsPage(SettingsViewModel model, AccountViewModel account, IClockFormat clock, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;
        _account = account;
        _language = language;
        _clock = clock;
        _undo = undo;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Listening only while on screen: the view model outlives this page (owner's finding on the Pixel 10). Colours
        // are resources, so a palette change needs no redraw here.
        _model.PropertyChanged += OnModelChanged;
        _language.Changed += OnLanguageChanged;
        _account.PropertyChanged += OnAccountChanged;

        // The first visit draws at once from the data loaded at start-up, before the page's first frame: drawing after
        // the refresh showed one blank frame (recorded on the Pixel 10).
        if (_body.Content is null)
            Render();
        await _model.RefreshAsync();
        if (_renderedLanguage != Strings.Language)
            QueueRender();

        // The latest name and email, without holding up the page; offline nothing changes (Spec 010 deliverable 5).
        Dispatcher.Dispatch(async () =>
        {
            try
            {
                await _account.RefreshAsync();
            }
            catch (Exception)
            {
                // Offline or failed: nothing changes, and a background refresh never takes the app down.
            }
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _model.PropertyChanged -= OnModelChanged;
        _language.Changed -= OnLanguageChanged;
        _account.PropertyChanged -= OnAccountChanged;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Toast))
        {
            if (!string.IsNullOrEmpty(_model.Toast))
                _undo.Say(_model.Toast);
            _model.Toast = "";
            return;
        }

        QueueRender();
    }

    // The account can change from a background refresh (a refused session), so everything goes through the dispatcher.
    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(() =>
    {
        if (e.PropertyName == nameof(AccountViewModel.Toast))
        {
            if (!string.IsNullOrEmpty(_account.Toast))
                _undo.Say(_account.Toast);
            _account.Toast = "";
            return;
        }

        QueueRender();
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await _model.RefreshAsync();
        QueueRender();
    });

    /// <summary>A refresh sets a dozen properties; they become one redraw, not twelve (review, owner's finding).</summary>
    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        // The time pickers write back while the page is built; those writes are not edits.
        _rendering = true;
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 96) };
        stack.Add(Ui.Text(Strings.Get("Tab.Settings"), "Title"));

        foreach (var view in AccountViews())
            stack.Add(view);

        stack.Add(Header("Settings.Baby"));
        stack.Add(Row(
            new VerticalStackLayout { Children = { Ui.Text(_model.BabyName, "BodyStrong"), Ui.Text(_model.BabyLine, "Meta", "Ink2") } },
            async () => await Navigation.PushAsync(_services.GetRequiredService<BabyPage>())));

        stack.Add(Header("Settings.Kinds"));
        var kinds = new VerticalStackLayout { Spacing = 4 };
        foreach (var kind in _model.Kinds)
        {
            var toggle = new Switch { IsToggled = kind.IsShown, IsEnabled = kind.CanChange, VerticalOptions = LayoutOptions.Center };
            toggle.SetDynamicResource(Switch.OnColorProperty, "Accent");
            var code = kind.Code;
            toggle.Toggled += (_, e) =>
            {
                if (!_rendering)
                    _model.SetKindShownCommand.Execute(new KindChange(code, e.Value));
            };
            var line = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], MinimumHeightRequest = 48 };
            line.Add(Ui.Text(kind.Name, "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
            line.Add(toggle, 1);
            kinds.Add(line);
        }

        stack.Add(new Card(kinds));
        stack.Add(Hint("Settings.KindsHint"));

        stack.Add(Header("Settings.Units"));
        stack.Add(new Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Text(Strings.Get("Settings.Volume"), "Label", "Ink2"),
                Choices(
                    (Strings.Get("Unit.Ml"), !_model.IsOunces, () => _model.SetUnitCommand.Execute(VolumeUnit.Millilitres)),
                    (Strings.Get("Unit.Oz"), _model.IsOunces, () => _model.SetUnitCommand.Execute(VolumeUnit.Ounces))),
            },
        }));
        stack.Add(Hint("Settings.ClockHint"));

        stack.Add(Header("Settings.Appearance"));
        var start = TimePicker(_model.QuietStart, t => _model.QuietStart = t);
        var end = TimePicker(_model.QuietEnd, t => _model.QuietEnd = t);
        var quiet = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10 };
        quiet.Add(Ui.Text(Strings.Get("Settings.QuietFrom"), "Label", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        quiet.Add(start, 1);
        quiet.Add(Ui.Text(Strings.Get("Settings.QuietTo"), "Label", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 2);
        quiet.Add(end, 3);
        stack.Add(new Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Choices(
                    (Strings.Get("Settings.Theme.Auto"), _model.Mode == AppearanceMode.Auto, () => _model.SetModeCommand.Execute(AppearanceMode.Auto)),
                    (Strings.Get("Settings.Theme.Day"), _model.Mode == AppearanceMode.Day, () => _model.SetModeCommand.Execute(AppearanceMode.Day)),
                    (Strings.Get("Settings.Theme.Night"), _model.Mode == AppearanceMode.Night, () => _model.SetModeCommand.Execute(AppearanceMode.Night))),
                Ui.Text(Strings.Get("Settings.AutoHint"), "Meta", "Ink2"),
                Ui.Text(Strings.Get("Settings.QuietHours"), "BodyStrong").Tap(l => l.Margin = new Thickness(0, 8, 0, 0)),
                quiet,
                Ui.Text(Strings.Get("Settings.QuietHint"), "Meta", "Ink2"),
            },
        }));

        stack.Add(Header("Settings.Language"));
        stack.Add(Row(Ui.Text(_model.LanguageLabel, "Body"), ChooseLanguageAsync));

        stack.Add(Header("Settings.About"));
        stack.Add(Ui.Text(_model.VersionLine, "Meta", "Ink2"));
        stack.Add(Row(Ui.Text(Strings.Get("Settings.Licences"), "Body"), async () => await Navigation.PushAsync(_services.GetRequiredService<LicencesPage>())));
        stack.Add(Row(Ui.Text(Strings.Get("Settings.Feedback"), "Body"), () => _model.SendFeedbackCommand.ExecuteAsync(null)));
        stack.Add(Row(Ui.Text(Strings.Get("Settings.Privacy"), "Body"), () => _model.OpenPrivacyCommand.ExecuteAsync(null)));

        _body.Content = stack;
        _renderedLanguage = Strings.Language;
        _rendering = false;
    }

    /// <summary>The Account section, first in Settings (Spec 010 §2).</summary>
    private IEnumerable<View> AccountViews()
    {
        yield return Header("Account.Title");
        if (!_account.IsSignedIn)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(Strings.Get("Account.SignIn"), "BodyStrong"), Ui.Text(_account.StatusLine, "Meta", "Ink2") } },
                () => _account.SignInCommand.ExecuteAsync(null));
            yield break;
        }

        yield return new Card(new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(_account.Name, "BodyStrong"), Ui.Text(_account.Email, "Meta", "Ink2") } });
        if (_account.StatusLine.Length > 0)
            yield return Ui.Text(_account.StatusLine, "Meta", "Ink2");
        yield return Row(Ui.Text(Strings.Get("Account.SignOut"), "Body"), ConfirmSignOutAsync);
        yield return Row(Ui.Text(Strings.Get("Account.Delete"), "Body"), ConfirmDeleteAsync);
    }

    private async Task ConfirmSignOutAsync()
    {
        // One dialog at a time: a second tap while the first is being prepared or shown does nothing.
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            var prompt = _account.SignOutPrompt;
            if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
                await _account.SignOutCommand.ExecuteAsync(null);
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            var prompt = await _account.DeletePromptAsync();
            if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
                await _account.DeleteAccountCommand.ExecuteAsync(null);
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ChooseLanguageAsync()
    {
        var options = _model.Languages;
        var chosen = await DisplayActionSheetAsync(Strings.Get("Settings.Language"), null, null, [.. options.Select(o => o.Label)]);
        if (options.FirstOrDefault(o => o.Label == chosen) is { } option)
            await _model.ChooseLanguageCommand.ExecuteAsync(option);
    }

    private TimePicker TimePicker(TimeSpan value, Action<TimeSpan> set)
    {
        var picker = new TimePicker { Time = value, Format = _clock.Is24Hour ? "HH:mm" : "h:mm tt", FontFamily = "FigtreeRegular", FontSize = 17 };
        picker.SetDynamicResource(Microsoft.Maui.Controls.TimePicker.TextColorProperty, "Ink");
        picker.PropertyChanged += (_, e) =>
        {
            if (_rendering || e.PropertyName != nameof(Microsoft.Maui.Controls.TimePicker.Time) || picker.Time is not { } time)
                return;
            set(time);
            _model.SaveQuietHoursCommand.Execute(null);
        };
        return picker;
    }

    private static Label Header(string key) => Ui.Text(Strings.Get(key), "Section").Tap(l => l.Margin = new Thickness(0, 16, 0, 0));

    private static Label Hint(string key) => Ui.Text(Strings.Get(key), "Meta", "Ink2");

    /// <summary>A tappable card row with a chevron.</summary>
    private View Row(View content, Func<Task> tapped)
    {
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], MinimumHeightRequest = 32 };
        grid.Add(content.Tap(c => c.VerticalOptions = LayoutOptions.Center), 0);
        grid.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 20).Tap(i => i.VerticalOptions = LayoutOptions.Center), 1);
        var card = new Card(grid);
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await RunTappedAsync(tapped)) });
        return card;
    }

    /// <summary>
    /// The tap's command is async void: nothing may escape it. A failure the view models do not turn into a toast (the
    /// phone's secret store refusing a write while signing in or out, say) shows the generic one instead of crashing.
    /// </summary>
    private async Task RunTappedAsync(Func<Task> tapped)
    {
        try
        {
            await tapped();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
        }
    }

    /// <summary>A segmented choice: the selected one is the primary button, the others ghost buttons.</summary>
    private static Grid Choices(params (string Text, bool Selected, Action Pick)[] choices)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < choices.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var (text, selected, pick) = choices[i];
            var button = new Button { Text = text };
            button.SetDynamicResource(VisualElement.StyleProperty, selected ? "PrimaryButton" : "GhostButton");
            button.Clicked += (_, _) => pick();
            grid.Add(button, i);
        }

        return grid;
    }
}
