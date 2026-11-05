using Coo.Domain.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// The Settings tab (Spec 009 §2–§3, Spec 010 §2, Spec 011b §2): account, family, baby, what you log, units, appearance, language, about.
/// One scrolling page; builds its UI tree once and updates dynamic labels and buttons in-place to prevent scroll jumping and lag.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _model;
    private readonly AccountViewModel _account;
    private readonly FamilySectionViewModel _family;
    private readonly IServiceProvider _services;
    private readonly IClockFormat _clock;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private readonly LanguageController _language;
    private readonly CooPageHeader _pageHeader;

    private readonly VerticalStackLayout _accountContainer = new() { Spacing = 10 };
    private readonly VerticalStackLayout _familyContainer = new() { Spacing = 10 };
    private readonly Label _babyNameLabel = Ui.Text("", "BodyStrong");
    private readonly Label _babyLineLabel = Ui.Text("", "Meta", "Ink2");
    private readonly VerticalStackLayout _kindsContainer = new() { Spacing = 4 };
    private readonly Dictionary<string, Switch> _kindSwitches = new(StringComparer.Ordinal);
    // Segmented choices, redrawn in place when the choice changes.
    private readonly ContentView _units = new();
    private readonly ContentView _themes = new();
    private TimePicker? _quietStartPicker;
    private TimePicker? _quietEndPicker;
    private readonly Label _languageLabel = Ui.Text("", "Body");
    private readonly Label _versionLabel = Ui.Text("", "Meta", "Ink2");

    private (bool IsSignedIn, string Name, string Email, string StatusLine, string LineActionText)? _accountCache;
    private (bool ShowsSwitcher, string ShowingTitle, string ShowingLine, bool ShowsMembers, string MembersLine, bool ShowsOnlyHere, bool ShowsInvite, bool ShowsBringOver)? _familyCache;

    private bool _confirming;
    private bool _rendering;
    private bool _renderQueued;
    private bool _familyRefreshQueued;
    private bool _opening;
    private string? _renderedLanguage;

    public SettingsPage(
        SettingsViewModel model, AccountViewModel account, FamilySectionViewModel family, IClockFormat clock, UndoService undo, LanguageController language,
        IServiceProvider services)
    {
        _model = model;
        _account = account;
        _family = family;
        _language = language;
        _clock = clock;
        _undo = undo;
        _services = services;
        _pageHeader = new CooPageHeader(services);
        _pageHeader.BabyChanged += () => Dispatcher.Dispatch(async () => await _model.RefreshAsync());
        Shell.SetNavBarIsVisible(this, false);

        var root = new Grid();
        root.Add(new Backdrop());
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
        _account.ConsentRequested += OnConsentRequested;
        _account.BringOverRequested += OnBringOverRequested;
        _family.PropertyChanged += OnFamilyChanged;
        _undo.DataChanged += OnDataChanged;
        var billing = _services.GetService<Coo.Mobile.Application.Billing.IBillingService>();
        if (billing != null) billing.EntitlementChanged += QueueRender;

#pragma warning disable CA1031 // async void: nothing may escape; the page keeps what it showed.
        try
        {
            if (_body.Content is null || _renderedLanguage != Strings.Language)
                BuildUi();
            else
                UpdateInPlace();

            _pageHeader.Refresh();
            await _model.RefreshAsync();
            await RunTappedAsync(_family.RefreshAsync);
            if (_renderedLanguage != Strings.Language)
                QueueRender();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031

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
        _account.ConsentRequested -= OnConsentRequested;
        _account.BringOverRequested -= OnBringOverRequested;
        _family.PropertyChanged -= OnFamilyChanged;
        _undo.DataChanged -= OnDataChanged;
        var billing = _services.GetService<Coo.Mobile.Application.Billing.IBillingService>();
        if (billing != null) billing.EntitlementChanged -= QueueRender;
    }

    // A pull can change the baby's profile or the family settings (units, what you log) under this page.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
#pragma warning disable CA1031 // Fire-and-forget: the page keeps what it showed; the next visit refreshes it.
        try
        {
            await _model.RefreshAsync();
            await _family.RefreshAsync();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    });

    // "Turn on sharing" and "Choose what to do with them" (Spec 011 §2–§4): the sheets open as modal pages.
    private void OnConsentRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(async () => await RunTappedAsync(() => _services.GetRequiredService<Sync.SyncPrompter>().ShowConsentAsync()));

    private void OnBringOverRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(async () => await RunTappedAsync(() => _services.GetRequiredService<Sync.SyncPrompter>().ShowBringOverAsync()));

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

        // The engine refreshes the family list before it reports a cycle, and signing in or out changes it.
        QueueFamilyRefresh();
        QueueRender();
    });

    // The family list can change from a background refresh; through the dispatcher like the account.
    private void OnFamilyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(() =>
    {
#pragma warning disable CA1031 // A dispatched handler: nothing may escape; the rows keep what they showed.
        try
        {
            if (e.PropertyName == nameof(FamilySectionViewModel.Toast))
            {
                if (!string.IsNullOrEmpty(_family.Toast))
                    _undo.Say(_family.Toast);
                _family.Toast = "";
                return;
            }

            QueueRender();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
#pragma warning disable CA1031 // Fire-and-forget: the page keeps what it showed; the next visit refreshes it.
        try
        {
            await _model.RefreshAsync();
            await _family.RefreshAsync();
            QueueRender();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    });

    /// <summary>A sync status change raises three account properties; they become one family read (main thread).</summary>
    private void QueueFamilyRefresh()
    {
        if (_familyRefreshQueued)
            return;
        _familyRefreshQueued = true;
        Dispatcher.Dispatch(async () =>
        {
            _familyRefreshQueued = false;
#pragma warning disable CA1031 // Fire-and-forget: the rows keep what they showed.
            try
            {
                await _family.RefreshAsync();
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        });
    }

    /// <summary>Coalesce property changes into a smooth in-place update without tearing down the view hierarchy.</summary>
    private void QueueRender()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _renderQueued = false;
#pragma warning disable CA1031
            try
            {
                if (_body.Content is null || _renderedLanguage != Strings.Language)
                    BuildUi();
                else
                    UpdateInPlace();
            }
            catch (Exception)
            {
                _rendering = false;
            }
#pragma warning restore CA1031
        });
    }

    private void BuildUi()
    {
        _rendering = true;
        _accountCache = null;
        _familyCache = null;
        _kindSwitches.Clear();

        var contentStack = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(PageFrame.Side, 0) };
        contentStack.Add(PageFrame.Title(Strings.Get("Tab.Settings")));
        contentStack.Add(_accountContainer);
        contentStack.Add(_familyContainer);

        // The rest is collected, then neighbouring rows are joined into grouped lists.
        var views = new List<View>();
        views.Add(Header("Settings.Baby"));
        views.Add(Row(
            new VerticalStackLayout { Children = { _babyNameLabel, _babyLineLabel } },
            async () => await Navigation.PushAsync(_services.GetRequiredService<BabyPage>())));

        views.Add(Header("Settings.Kinds"));
        views.Add(new Card(_kindsContainer));
        views.Add(Hint("Settings.KindsHint"));

        views.Add(Header("Settings.Units"));

        views.Add(new Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Text(Strings.Get("Settings.Volume"), "Label", "Ink2"),
                _units,
            },
        }));
        views.Add(Hint("Settings.ClockHint"));

        views.Add(Header("Reminders.Title"));
        views.Add(Row(Ui.Text(Strings.Get("Reminders.Title"), "Body"), async () => await Navigation.PushAsync(_services.GetRequiredService<RemindersPage>())));

        views.Add(Header("Import.Header"));
        views.Add(Row(Ui.Text(Strings.Get("Import.Title"), "Body"), ImportAsync));
        views.Add(Row(Ui.Text(Strings.Get("Export.Title"), "Body"), ExportAsync));
        views.Add(Hint("Import.Hint"));

        views.Add(Header("Premium"));
        views.Add(Row(Ui.Text(Strings.Get("Premium.Title") ?? "Mochi Premium", "Body"), async () => await Navigation.PushAsync(_services.GetRequiredService<PremiumPage>())));

        views.Add(Header("Settings.Appearance"));

        _quietStartPicker = TimePicker(_model.QuietStart, t => _model.QuietStart = t);
        _quietEndPicker = TimePicker(_model.QuietEnd, t => _model.QuietEnd = t);
        var quiet = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10 };
        quiet.Add(Ui.Text(Strings.Get("Settings.QuietFrom"), "Label", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 0);
        quiet.Add(Ui.Rounded(_quietStartPicker, "Glass", 12, new Thickness(12, 4), "GlassLine"), 1);
        quiet.Add(Ui.Text(Strings.Get("Settings.QuietTo"), "Label", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center), 2);
        quiet.Add(Ui.Rounded(_quietEndPicker, "Glass", 12, new Thickness(12, 4), "GlassLine"), 3);

        views.Add(new Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                _themes,
                Ui.Text(Strings.Get("Settings.AutoHint"), "Meta", "Ink2"),
                Ui.Text(Strings.Get("Settings.QuietHours"), "BodyStrong").Tap(l => l.Margin = new Thickness(0, 8, 0, 0)),
                quiet,
                Ui.Text(Strings.Get("Settings.QuietHint"), "Meta", "Ink2"),
            },
        }));

        views.Add(Header("Settings.Language"));
        views.Add(Row(_languageLabel, ChooseLanguageAsync));

        views.Add(Header("Settings.About"));
        views.Add(_versionLabel.Tap(l => l.Margin = new Thickness(4, 0)));
        // "How Coo works": the tour from the first start, again (owner, 2026-11-03).
        views.Add(Row(Ui.Text(Strings.Get("Tour.Title"), "Body"), async () =>
        {
            var tour = _services.GetRequiredService<TourPage>();
            ((Coo.Mobile.Presentation.Tour.TourViewModel)tour.BindingContext!).IsReplay = true;
            await Navigation.PushAsync(tour);
        }));
        views.Add(Row(Ui.Text(Strings.Get("Settings.Licences"), "Body"), async () => await Navigation.PushAsync(_services.GetRequiredService<LicencesPage>())));
        views.Add(Row(Ui.Text(Strings.Get("Settings.Feedback"), "Body"), () => _model.SendFeedbackCommand.ExecuteAsync(null)));
        views.Add(Row(Ui.Text(Strings.Get("Settings.Privacy"), "Body"), () => _model.OpenPrivacyCommand.ExecuteAsync(null)));

        foreach (var view in Grouped(views))
            contentStack.Add(view);

        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(0, 0, 0, 96),
            Spacing = 4,
            Children = { _pageHeader, contentStack }
        };

        _body.Content = stack;
        _renderedLanguage = Strings.Language;
        _rendering = false;

        UpdateInPlace();
    }

    private void UpdateInPlace()
    {
        _rendering = true;
        try
        {
            _babyNameLabel.Text = _model.BabyName;
            _babyLineLabel.Text = _model.BabyLine;
            _languageLabel.Text = _model.LanguageLabel;
            _versionLabel.Text = _model.VersionLine;

            UpdateAccountViews();
            UpdateFamilyViews();
            UpdateKinds();
            UpdateUnits();
            UpdateThemes();

            if (_quietStartPicker != null && _quietStartPicker.Time != _model.QuietStart)
                _quietStartPicker.Time = _model.QuietStart;
            if (_quietEndPicker != null && _quietEndPicker.Time != _model.QuietEnd)
                _quietEndPicker.Time = _model.QuietEnd;
        }
        finally
        {
            _rendering = false;
        }
    }

    private void UpdateKinds()
    {
        if (_kindSwitches.Count != _model.Kinds.Count || !_model.Kinds.All(k => _kindSwitches.ContainsKey(k.Code)))
        {
            _kindSwitches.Clear();
            _kindsContainer.Children.Clear();
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
                _kindSwitches[code] = toggle;
                _kindsContainer.Add(line);
            }
            return;
        }

        foreach (var kind in _model.Kinds)
        {
            if (_kindSwitches.TryGetValue(kind.Code, out var sw))
            {
                if (sw.IsToggled != kind.IsShown)
                    sw.IsToggled = kind.IsShown;
                if (sw.IsEnabled != kind.CanChange)
                    sw.IsEnabled = kind.CanChange;
            }
        }
    }

    private void UpdateUnits() => _units.Content = PageFrame.Segmented(
        [Strings.Get("Unit.Ml"), Strings.Get("Unit.Oz")],
        _model.IsOunces ? 1 : 0,
        i => _model.SetUnitCommand.Execute(i == 0 ? VolumeUnit.Millilitres : VolumeUnit.Ounces));

    private static readonly AppearanceMode[] Modes = [AppearanceMode.Auto, AppearanceMode.Day, AppearanceMode.Night];

    private void UpdateThemes() => _themes.Content = PageFrame.Segmented(
        [Strings.Get("Settings.Theme.Auto"), Strings.Get("Settings.Theme.Day"), Strings.Get("Settings.Theme.Night")],
        Array.IndexOf(Modes, _model.Mode),
        i => _model.SetModeCommand.Execute(Modes[i]));

    private void UpdateAccountViews()
    {
        var current = (_account.IsSignedIn, _account.Name, _account.Email, _account.StatusLine, _account.LineActionText);
        if (_accountCache == current && _accountContainer.Children.Count > 0)
            return;
        _accountCache = current;
        _accountContainer.Children.Clear();
        foreach (var view in Grouped(AccountViews()))
            _accountContainer.Add(view);
    }

    private void UpdateFamilyViews()
    {
        var current = (_family.ShowsSwitcher, _family.ShowingTitle, _family.ShowingLine, _family.ShowsMembers, _family.MembersLine, _family.ShowsOnlyHere, _family.ShowsInvite, _family.ShowsBringOver);
        if (_familyCache == current && _familyContainer.Children.Count > 0)
            return;
        _familyCache = current;
        _familyContainer.Children.Clear();
        foreach (var view in Grouped(FamilyViews()))
            _familyContainer.Add(view);
    }

    /// <summary>The Account section, first in Settings (Spec 010 §2).</summary>
    private IEnumerable<View> AccountViews()
    {
        yield return Header("Account.Title");
        if (!_account.IsSignedIn)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(Strings.Get("Account.SignIn"), "BodyStrong"), Ui.Text(_account.StatusLine, "Meta", "Ink2") } },
                SignInAsync);
            yield break;
        }

        yield return Row(new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(_account.Name, "BodyStrong"), Ui.Text(_account.Email, "Meta", "Ink2") } }, null);
        if (_account.StatusLine.Length > 0)
            yield return Ui.Text(_account.StatusLine, "Meta", "Ink2");
        if (_account.LineActionText.Length > 0)
        {
            yield return Row(Ui.Text(_account.LineActionText, "Body"), () =>
            {
                _account.LineActionCommand.Execute(null);
                return Task.CompletedTask;
            });
        }

        yield return Row(Ui.Text(Strings.Get("Account.SignOut"), "Body"), ConfirmSignOutAsync);
        yield return Row(Ui.Text(Strings.Get("Account.Delete"), "Body"), ConfirmDeleteAsync);
    }

    /// <summary>Settings → Family (Spec 011b §2), right after Account.</summary>
    private IEnumerable<View> FamilyViews()
    {
        yield return Header("Family.Title");
        if (_family.ShowsSwitcher)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(_family.ShowingTitle, "BodyStrong"), Ui.Text(_family.ShowingLine, "Meta", "Ink2") } },
                ChooseFamilyAsync);
        }

        if (_family.ShowsMembers && _family.ShownId is { } shown)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(Strings.Get("Family.Members"), "BodyStrong"), Ui.Text(_family.MembersLine, "Meta", "Ink2") } },
                () => OpenOnceAsync(() => Navigation.PushAsync(FamilyPage.Open(_services, shown))));
        }

        if (_family.ShowsOnlyHere)
            yield return Ui.Text(Strings.Get("Family.OnlyHere"), "Meta", "Ink2");
        if (_family.ShowsInvite)
            yield return Row(Ui.Text(Strings.Get("Family.InviteSomeone"), "Body"), async () =>
            {
                if (await InviteRole.AskAsync(this) is { } role)
                    await _family.InviteCommand.ExecuteAsync(role);
            });
        yield return Row(Ui.Text(Strings.Get("Family.Join"), "Body"), () => _services.GetRequiredService<Sync.SyncPrompter>().ShowEnterCodeAsync());
        if (_family.ShowsBringOver)
        {
            yield return Row(
                Ui.Text(Strings.Get("Family.BringOver"), "Body"),
                () => OpenOnceAsync(() => _services.GetRequiredService<Presentation.Sheets.SheetNavigator>().PushAsync(stack =>
                    // Nothing over a sheet the prompter has open or is still opening (review M4).
                    stack.Count > 0 ? null : _services.GetRequiredService<FamilyBringOverPage>())));
        }
    }

    /// <summary>
    /// The switcher (§7.3): "Show which family?", every family but soft-locked ones, as Coo's own sheet (walk fix F2). Its row
    /// actions open the bring-over into the shown family, or the Family page for a leave or a delete.
    /// </summary>
    private async Task ChooseFamilyAsync()
    {
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            await _services.GetRequiredService<Presentation.Sheets.SheetNavigator>().PushAsync(stack =>
                stack.Count > 0 ? null : FamilySwitcherPage.Open(_services, _family, FamilyActionAsync));
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>A switcher row's action, once the sheet has closed: Remove from this phone stays in the sheet.</summary>
    private async Task FamilyActionAsync(FamilyRow row, FamilyRowAction action)
    {
        switch (action)
        {
            case FamilyRowAction.BringHere when _family.ShownId is { } shown:
                await OpenOnceAsync(() => _services.GetRequiredService<Presentation.Sheets.SheetNavigator>().PushAsync(stack =>
                    stack.Count > 0 ? null : FamilyBringOverPage.Into(_services, row.Id, shown)));
                break;
            case FamilyRowAction.Leave or FamilyRowAction.Delete:
                await OpenOnceAsync(() => Navigation.PushAsync(FamilyPage.Open(_services, row.Id)));
                break;
        }
    }

    /// <summary>A second tap while a page is being pushed opens nothing more.</summary>
    private async Task OpenOnceAsync(Func<Task> open)
    {
        if (_opening)
            return;
        _opening = true;
        try
        {
            await open();
        }
        finally
        {
            _opening = false;
        }
    }

    /// <summary>
    /// Google's sheet, then Coo's loading page until the account's families are on the phone (at most 20 seconds), so the
    /// pages do not jump from this phone's family to the account's one by one.
    /// </summary>
    private async Task SignInAsync()
    {
        await _account.SignInCommand.ExecuteAsync(null);
        if (!_account.IsSignedIn)
            return;
        await Components.LoadingOverlay.RunAsync(Strings.Get("Loading.Family"), async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await _services.GetRequiredService<Coo.Mobile.Application.Sync.ISyncControl>().SyncNowAsync(timeout.Token);
            }
#pragma warning disable CA1031 // Offline or slow: the pages fill in when sync catches up.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
    }

    private async Task ConfirmSignOutAsync()
    {
        // One dialog at a time: a second tap while the first is being prepared or shown does nothing.
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            var prompt = await _account.SignOutPromptAsync();
            if (await Components.CooDialog.ConfirmAsync(this, prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
                await Components.LoadingOverlay.RunAsync(Strings.Get("Loading.SignOut"), () => _account.SignOutCommand.ExecuteAsync(null));
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
            if (await Components.CooDialog.ConfirmAsync(this, prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
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
        var chosen = await Components.CooDialog.ChooseAsync(this, Strings.Get("Settings.Language"), [.. options.Select(o => o.Label)]);
        if (chosen >= 0)
            await _model.ChooseLanguageCommand.ExecuteAsync(options[chosen]);
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

    private static Label Header(string key) => PageFrame.SectionLabel(Strings.Get(key)).Tap(l => l.Margin = new Thickness(0, 12, 0, -4));

    private static Label Hint(string key) => Ui.Text(Strings.Get(key), "Meta", "Ink2").Tap(l => l.Margin = new Thickness(4, 0));

    /// <summary>One row of a grouped list, with a chevron when it opens something; <see cref="Grouped"/> joins neighbours into one card.</summary>
    private View Row(View content, Func<Task>? tapped)
    {
        var grid = new GroupRow { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], MinimumHeightRequest = 52, Padding = new Thickness(16, 12), ColumnSpacing = 12 };
        grid.Add(content.Tap(c => c.VerticalOptions = LayoutOptions.Center), 0);
        if (tapped is null)
            return grid;
        grid.Add(new Icon(IconGeometry.ChevronRight, "Ink2", 18).Tap(i => i.VerticalOptions = LayoutOptions.Center), 1);
        grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await RunTappedAsync(tapped)) });
        return grid;
    }

    /// <summary>Rows next to each other become one glass group with hairlines between them (the design's grouped lists).</summary>
    private static IEnumerable<View> Grouped(IEnumerable<View> views)
    {
        var rows = new List<View>();
        foreach (var view in views)
        {
            if (view is GroupRow)
            {
                rows.Add(view);
                continue;
            }

            if (rows.Count > 0)
                yield return PageFrame.Group(rows);
            rows = [];
            yield return view;
        }

        if (rows.Count > 0)
            yield return PageFrame.Group(rows);
    }

    /// <summary>A row of a grouped list (see <see cref="Row"/>).</summary>
    private sealed class GroupRow : Grid;

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

    /// <summary>The whole log as a CSV file, to the share sheet: made on the phone, sent only where the parent chooses.</summary>
    private async Task ExportAsync()
    {
        try
        {
            var csv = await _services.GetRequiredService<Coo.Mobile.Application.Import.ExportLog>().ExecuteAsync(CancellationToken.None);
            if (csv is null)
                return;
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, $"coo-log-{_services.GetRequiredService<TimeProvider>().GetLocalNow():yyyy-MM-dd}.csv");
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await Share.Default.RequestAsync(new ShareFileRequest { Title = Strings.Get("Export.Title"), File = new ShareFile(path, "text/csv") });
        }
#pragma warning disable CA1031 // A failed export says so; it never takes Settings down.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Strings.Get("Export.Title"), Strings.Get("Report.Failed"), Strings.Get("Join.OK"));
        }
    }

    /// <summary>Another app's export: pick the file, see what Coo found, and only then save it.</summary>
    private async Task ImportAsync()
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Strings.Get("Import.Title") });
            if (file is null)
                return;
            await using var stream = await file.OpenReadAsync();
            if (stream.CanSeek && stream.Length > Coo.Mobile.Presentation.Settings.ImportViewModel.MaxFileBytes)
            {
                await Components.CooDialog.AlertAsync(this, Strings.Get("Import.Title"), Strings.Get("Import.Nothing"), Strings.Get("Join.OK"));
                return;
            }

            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            var model = _services.GetRequiredService<Coo.Mobile.Presentation.Settings.ImportViewModel>();
            if (await model.PreviewAsync(text) is not { } preview || preview.Entries.Count == 0)
            {
                await Components.CooDialog.AlertAsync(this, Strings.Get("Import.Title"), Strings.Get("Import.Nothing"), Strings.Get("Join.OK"));
                return;
            }

            var describe = Coo.Mobile.Presentation.Settings.ImportViewModel.Describe(preview);
            if (!await Components.CooDialog.ConfirmAsync(this, Strings.Get("Import.Title"), describe, Strings.Get("Import.Go"), Strings.Get("Action.Cancel")))
                return;
            var result = await model.ImportAsync(preview);
            await Components.CooDialog.AlertAsync(this, Strings.Get("Import.Title"), Coo.Mobile.Presentation.Settings.ImportViewModel.Describe(result), Strings.Get("Join.OK"));
        }
#pragma warning disable CA1031 // An unreadable file says so; it never takes Settings down.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Strings.Get("Import.Title"), Strings.Get("Import.Nothing"), Strings.Get("Join.OK"));
        }
    }
}
