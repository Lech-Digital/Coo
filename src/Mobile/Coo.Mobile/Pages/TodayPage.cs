using System.Collections.Specialized;
using System.ComponentModel;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>Today (Spec 006 §4–§6): the sections for the current stage and palette, the sheets, and the toast.</summary>
public sealed class TodayPage : ContentPage
{
    private readonly TodayViewModel _model;
    private readonly IServiceProvider _services;
    private readonly VerticalStackLayout _sections = new() { Padding = new Thickness(0, 0, 0, 24) };
    private readonly Sheet _sheet = new();
    private readonly UndoService _undo;
    private readonly LanguageController _language;
    private IDispatcherTimer? _tick;
    private bool _pageSheet;
    private bool _rebuildQueued;
    private IReadOnlyList<IHomeSection> _built = [];
    private string? _builtLanguage;

    public TodayPage(TodayViewModel model, UndoService undo, LanguageController language, IServiceProvider services)
    {
        _model = model;

        // A running timer goes in the notification bar: asked once, the first time one starts, never again after "Not now".
        _model.PrimeNotificationsAsync = async () =>
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() == PermissionStatus.Granted)
                return true;
            if (Preferences.Default.Get(TimerNotifyAskedKey, false))
                return false;
            Preferences.Default.Set(TimerNotifyAskedKey, true);
            return await Components.CooDialog.PrimeAsync<Permissions.PostNotifications>(this, "Permission.NotifyTitle", "Permission.NotifyBody");
        };
        _undo = undo;
        _language = language;
        _services = services;
        Shell.SetNavBarIsVisible(this, false);

        // "Earlier…" is the page's own sheet, not the view model's, so a tap outside hides it here. Closing it through
        // the view model left it open and forgot it, and Back then sent Coo to the background (owner, Pixel 10).
        _sheet.Closed += async (_, _) =>
        {
            if (_pageSheet)
            {
                _pageSheet = false;
                await _sheet.HideAsync();
                return;
            }

            model.CloseSheetCommand.Execute(null);
        };


        var root = new Grid();
        root.Add(new ScrollView { Content = _sections });
        root.Add(new UndoToastView(undo));
        root.Add(_sheet);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Listening only while on screen: the view model outlives this page (Spec 009, owner's finding on the Pixel 10).
        _undo.DataChanged += OnDataChanged;
        _model.Sections.CollectionChanged += OnSectionsChanged;
        _model.PropertyChanged += OnModelChanged;
        _language.Changed += OnLanguageChanged;
        await _model.NoteActiveAsync();
        await _model.RefreshAsync();

        // The section views update themselves; they are rebuilt only when the sections or the language changed.
        if (!_built.SequenceEqual(_model.Sections) || _builtLanguage != Strings.Language)
            QueueRebuild();
        _tick ??= Dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += OnTick;
        _tick.Start();

        VoiceShortcut.Requested += OnVoiceShortcut;
        if (VoiceShortcut.TryTake())
            await OpenVoiceAsync(listen: true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _undo.DataChanged -= OnDataChanged;
        _model.Sections.CollectionChanged -= OnSectionsChanged;
        _model.PropertyChanged -= OnModelChanged;
        _language.Changed -= OnLanguageChanged;
        VoiceShortcut.Requested -= OnVoiceShortcut;
        if (_tick is not null)
        {
            _tick.Stop();
            _tick.Tick -= OnTick;
        }
    }

    /// <summary>Back closes an open sheet before it leaves the page (Spec 006 §2).</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_pageSheet)
        {
            _ = _sheet.HideAsync();
            _pageSheet = false;
            return true;
        }

        if (_model.Sheet == HomeSheet.None)
            return base.OnBackButtonPressed();
        _model.CloseSheetCommand.Execute(null);
        return true;
    }

    // After an undo, or a delete that the window commits, the sections show what is now stored.
    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () => await _model.RefreshAsync());

    private void OnTick(object? sender, EventArgs e) => _model.Tick();

    private async void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodayViewModel.Sheet))
            await ShowSheetAsync();
        else if (e.PropertyName == nameof(TodayViewModel.Toast))
            ShowToast(_model.Toast);
    }

    // A palette change clears and refills the sections: seven events, one rebuild.
    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        await _model.RefreshAsync();
        QueueRebuild();
    });

    private void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        foreach (var old in _sections.Children)
            LiveView.Release(old as IVisualTreeElement);
        _sections.Children.Clear();
        foreach (var section in _model.Sections)
            _sections.Add(SectionViews.Create(section, new SectionHost(OpenDebug, OpenEntry, OpenAttention, OpenEarlier, _model, SwitchToLog, SwitchToAdvisor, OpenVoice, OpenHealth, OpenSleepWhy, OpenPremium, OpenSleepPlan, ChooseBaby, OpenMilkStash, ShareText)));
        _built = [.. _model.Sections];
        _builtLanguage = Strings.Language;
    }

    /// <summary>Twins and siblings: which baby this phone shows.</summary>
    private void ChooseBaby() => Dispatcher.Dispatch(async () =>
    {
        var siblings = _model.Siblings;
        if (_model.Baby is null || siblings.Count == 0)
            return;
        var choice = await CooDialog.ChooseAsync(this, Strings.Get("Sibling.Choose"), [.. siblings.Select(b => b.Name)]);
        if (choice >= 0 && choice < siblings.Count)
            await _model.FocusAsync(siblings[choice].Id);
    });

    /// <summary>The caregiver's handover, out through Android's share sheet (a message to the parent).</summary>
    private void ShareText(string text) => Dispatcher.Dispatch(async () =>
        await Share.Default.RequestAsync(new ShareTextRequest { Text = text, Title = Strings.Get("Handover.Share") }));

    private void OpenMilkStash() => Dispatcher.Dispatch(async () => await Navigation.PushAsync(_services.GetRequiredService<MilkStashPage>()));

    private void OpenSleepPlan() => Dispatcher.Dispatch(async () => await Navigation.PushAsync(_services.GetRequiredService<SleepPlanPage>()));

    private void SwitchToLog() => Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//log"));

    private void SwitchToAdvisor() => Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//advisor"));

    private async void OpenEntry(EntryId id)
    {
        await Navigation.PushAsync(await EditPage.ForEntryAsync(_services, id));
    }

    private async void OpenAttention() => await Navigation.PushAsync(_services.GetRequiredService<AttentionPage>());

    private const string QuickLogConsentKey = "ai.logging.consent.v2";
    private const string TimerNotifyAskedKey = "timers.notify.asked";
    private Components.QuickLogView? _voice;

    /// <summary>
    /// "Tell Coo" (ADR 0005): asks once for consent to send the words to Gemini, then opens the sheet listening, so one
    /// tap is enough to start talking. The sheet closes itself after Save or Cancel.
    /// </summary>
    public async void OpenVoice() => await OpenVoiceAsync(listen: true);

    internal async Task OpenVoiceAsync(bool listen)
    {
        if (!Preferences.Default.Get(QuickLogConsentKey, false))
        {
            var agreed = await Components.CooDialog.ConfirmAsync(this, Strings.Get("Quick.ConsentTitle"), Strings.Get("Quick.ConsentBody"), Strings.Get("Advisor.ConsentAgree"), Strings.Get("Advisor.ConsentLater"));
            if (!agreed)
                return;
            Preferences.Default.Set(QuickLogConsentKey, true);
        }

        var model = _services.GetRequiredService<Coo.Mobile.Presentation.QuickLog.QuickLogViewModel>();
        await model.OpenAsync();
        model.Finished -= OnVoiceFinished;
        model.Finished += OnVoiceFinished;
        _voice?.Detach();
        _voice = new Components.QuickLogView(model);
        _pageSheet = true;
        await _sheet.ShowAsync(_voice);
        if (listen && model.CanListen && model.PendingNotes.Count == 0
            && await Components.CooDialog.PrimeAsync<Permissions.Microphone>(this, "Permission.MicTitle", "Permission.MicBody"))
            model.ListenCommand.Execute(null);
    }

    // The "Tell Coo" shortcut while Today is on screen; when it is not, App brings Today forward and OnAppearing takes it.
    private void OnVoiceShortcut(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        if (VoiceShortcut.TryTake())
            await OpenVoiceAsync(listen: true);
    });

    private void OnVoiceFinished(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        _pageSheet = false;
        await _sheet.HideAsync();
        await _model.RefreshAsync();
    });

    /// <summary>"Earlier…" below the tiles: which kind, then the form (Spec 007 §4).</summary>
    private async void OpenEarlier()
    {
        _pageSheet = true;
        // Hidden kinds offer no "Earlier…" choice (Spec 009 §3.2).
        var items = new (string Kind, View Item)[]
        {
            ("feeding", EarlierItem("feeding", "Kind.Feed", "Earlier.FeedSubtitle")),
            ("feeding", EarlierItem("feeding", "Feed.Bottle", "Earlier.BottleSubtitle", bottle: true)),
            ("diaper", EarlierItem("diaper", "Kind.Diaper", "Earlier.DiaperSubtitle")),
            ("sleep", EarlierItem("sleep", "Kind.Sleep", "Earlier.SleepSubtitle")),
            ("pump", EarlierItem("pump", "Kind.Pump", "Earlier.PumpSubtitle")),
            ("bath", EarlierItem("bath", "Kind.Bath", "Earlier.BathSubtitle")),
            ("tummy", EarlierItem("tummy", "Earlier.TummyStart", "", subtitle: _model.TummySubtitle(), onTap: async () =>
            {
                _pageSheet = false;
                await _sheet.HideAsync();
                await _model.StartTummyAsync();
            })),
            ("tummy", EarlierItem("tummy", "Kind.Tummy", "Earlier.TummyEarlierSubtitle")),
            ("solids", EarlierItem("solids", "Kind.Solids", "Earlier.SolidsSubtitle")),
            ("milestone", EarlierItem("milestone", "Kind.Milestone", "Earlier.MilestoneSubtitle")),
        }.Where(b => _model.IsShown(b.Kind)).Select(b => b.Item).ToArray();
        await _sheet.ShowAsync(Panel(Strings.Get("Edit.Earlier"), items));
    }

    /// <summary>Why this sleep window (ADR 0008 §8): the explanation, one tap from Today, with the note that it is not advice.</summary>
    private async void OpenSleepWhy(NextUpSection section)
    {
        _pageSheet = true;
        var lines = section.NapWhy.Select((line, i) => (View)Ui.Text(line, i == section.NapWhy.Count - 1 ? "Meta" : "Body", "Ink2")).ToList();
        lines.Insert(0, Ui.Text(section.NextNap, "BodyStrong"));
        await _sheet.ShowAsync(Panel(section.NapWhyTitle, [.. lines]));
    }

    private async void OpenPremium() => await Navigation.PushAsync(_services.GetRequiredService<PremiumPage>());

    /// <summary>"Health": the growth chart, then a measurement, medicine, temperature, vaccine or visit, each a form set to now.</summary>
    private async void OpenHealth()
    {
        _pageSheet = true;
        var chart = EarlierItem("growth", "Growth.Chart", "Growth.ChartSubtitle", icon: Coo.Mobile.Presentation.Theme.IconGeometry.Chart);
        chart.GestureRecognizers.Clear();
        chart.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                _pageSheet = false;
                await _sheet.HideAsync();
                await Navigation.PushAsync(_services.GetRequiredService<GrowthPage>());
            }),
        });
        var photo = EarlierItem("growth", "Health.Photo", "Health.PhotoSubtitle", icon: Coo.Mobile.Presentation.Theme.IconGeometry.Camera);
        photo.GestureRecognizers.Clear();
        photo.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(OpenPhoto) });
        View[] items =
        [
            chart,
            photo,
            EarlierItem("growth", "Kind.Growth", "Health.GrowthSubtitle"),
            EarlierItem("medicine", "Kind.Medicine", "Health.MedicineSubtitle"),
            EarlierItem("temperature", "Kind.Temperature", "Health.TemperatureSubtitle"),
            EarlierItem("vaccine", "Kind.Vaccine", "Health.VaccineSubtitle"),
            EarlierItem("visit", "Kind.Visit", "Health.VisitSubtitle"),
        ];
        await _sheet.ShowAsync(Panel(Strings.Get("Health.Title"), items));
    }

    /// <summary>
    /// "From a photo": what Coo reads and where the photo goes, said before it is taken (the names on a record cannot be
    /// hidden first), then the camera or the gallery.
    /// </summary>
    private async void OpenPhoto()
    {
        _pageSheet = true;
        var take = new Button { Text = Strings.Get("Photo.Take") };
        take.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        take.Clicked += async (_, _) => await ReadPhotoAsync(camera: true);
        var choose = new Button { Text = Strings.Get("Photo.Choose") };
        choose.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        choose.Clicked += async (_, _) => await ReadPhotoAsync(camera: false);
        await _sheet.ShowAsync(Panel(
            Strings.Get("Photo.Title"),
            Ui.Text(Strings.Get("Photo.Explain"), "Body", "Ink2"),
            Row(take, choose),
            Ui.Text(Strings.Get("Photo.Privacy"), "Meta", "Ink2")));
    }

    private async Task ReadPhotoAsync(bool camera)
    {
        if (camera && !await Components.CooDialog.PrimeAsync<Permissions.Camera>(this, "Permission.CameraTitle", "Permission.CameraBody"))
            return;
        FileResult? photo;
        try
        {
            photo = camera
                ? await MediaPicker.Default.CapturePhotoAsync()
                : (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault();
        }
        catch (Exception e) when (e is PermissionException or FeatureNotSupportedException or InvalidOperationException)
        {
            photo = null;
        }

        if (photo is null)
            return;

        byte[] jpeg;
        await using (var stream = await photo.OpenReadAsync())
            jpeg = await Task.Run(() => Scaled(stream));

        var model = _services.GetRequiredService<Coo.Mobile.Presentation.QuickLog.QuickLogViewModel>();
        await model.OpenAsync();
        model.Finished -= OnVoiceFinished;
        model.Finished += OnVoiceFinished;
        _voice?.Detach();
        _voice = new Components.QuickLogView(model);
        _pageSheet = true;
        await _sheet.ShowAsync(_voice);
        await model.ReadPhotoAsync(jpeg);
    }

    /// <summary>At most 1600 pixels on the long side, as a JPEG: sharp enough to read handwriting, a few hundred kilobytes.</summary>
    private static byte[] Scaled(Stream stream)
    {
        var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
        var scaled = image.Width > 1600 || image.Height > 1600 ? image.Downsize(1600, disposeOriginal: true) : image;
        return scaled.AsBytes(Microsoft.Maui.Graphics.ImageFormat.Jpeg, 0.85f);
    }

    /// <param name="subtitle">The line under the title, when it is not a fixed string (<paramref name="subtitleKey"/>).</param>
    /// <param name="onTap">What a tap does instead of opening the form: tummy time starts its timer.</param>
    private View EarlierItem(string kindCode, string key, string subtitleKey, bool bottle = false, string? icon = null, string? subtitle = null, Func<Task>? onTap = null)
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For(kindCode);
        var iconPath = icon ?? kindCode switch
        {
            "feeding" when bottle => Coo.Mobile.Presentation.Theme.IconGeometry.Feed,
            "feeding" => Coo.Mobile.Presentation.Theme.IconGeometry.SideLeft,
            "diaper" => Coo.Mobile.Presentation.Theme.IconGeometry.Diaper,
            "sleep" => Coo.Mobile.Presentation.Theme.IconGeometry.Sleep,
            "pump" => Coo.Mobile.Presentation.Theme.IconGeometry.Pump,
            _ => look.Icon,
        };

        var iconContainer = new Border
        {
            WidthRequest = 44,
            HeightRequest = 44,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            StrokeThickness = 0,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Content = new Icon(iconPath, look.Ink, 22).Tap(i =>
            {
                i.HorizontalOptions = LayoutOptions.Center;
                i.VerticalOptions = LayoutOptions.Center;
            }),
        };
        iconContainer.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);

        var titleLabel = Ui.Text(Strings.Get(key), "BodyStrong");
        var subtitleText = subtitle ?? Strings.Get(subtitleKey);
        var subtitleLabel = Ui.Text(string.IsNullOrEmpty(subtitleText) ? "" : subtitleText, "Meta", "Ink2");

        var textStack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { titleLabel, subtitleLabel },
        };

        var chevron = new Icon(Coo.Mobile.Presentation.Theme.IconGeometry.ChevronRight, "Ink2", 18).Tap(c =>
        {
            c.HorizontalOptions = LayoutOptions.End;
            c.VerticalOptions = LayoutOptions.Center;
        });

        var rowGrid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ],
            ColumnSpacing = 14,
            Padding = new Thickness(14, 12),
        };
        rowGrid.Add(iconContainer, 0);
        rowGrid.Add(textStack, 1);
        rowGrid.Add(chevron, 2);

        var border = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            StrokeThickness = 1,
            Content = rowGrid,
            Margin = new Thickness(0, 4),
        };
        border.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        border.SetDynamicResource(Border.StrokeProperty, "LineBrush");

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await (onTap?.Invoke() ?? OpenNewAsync(kindCode, bottle));
        border.GestureRecognizers.Add(tap);

        return border;
    }

    private View EarlierLink(string kindCode, bool bottle = false)
    {
        var button = new Button
        {
            Text = $"🕒 {Strings.Get("Edit.Earlier")}",
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            CornerRadius = 18,
            FontFamily = "FigtreeMedium",
            FontSize = 13,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 6, 0, 0),
        };
        button.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        button.SetDynamicResource(Button.TextColorProperty, "Ink2");
        button.SetDynamicResource(Button.BorderColorProperty, "Line");
        button.BorderWidth = 1;
        button.Clicked += async (_, _) => await OpenNewAsync(kindCode, bottle);
        return button;
    }

    private async Task OpenNewAsync(string kindCode, bool bottle)
    {
        _pageSheet = false;
        _model.CloseSheetCommand.Execute(null);
        await _sheet.HideAsync();
        var page = _services.GetRequiredService<EditPage>();
        await page.NewAsync(kindCode, bottle);
        await Navigation.PushAsync(page);
    }

    private void OpenDebug()
    {
#if DEBUG_SCREEN
        Navigation.PushAsync(_services.GetRequiredService<DebugScreen.DebugPage>());
#endif
    }

    private async Task ShowSheetAsync()
    {
        View? content = _model.Sheet switch
        {
            HomeSheet.Feed => FeedSheet(),
            HomeSheet.Bottle => BottleSheet(),
            HomeSheet.Diaper => DiaperSheet(),
            HomeSheet.PumpYield => PumpSheet(),
            HomeSheet.RecordBirth => BirthSheet(),
            HomeSheet.AwakeCheck => AwakeSheet(),
            _ => null,
        };

        if (content is null)
            await _sheet.HideAsync();
        else
            await _sheet.ShowAsync(content);
    }

    /// <summary>
    /// Feed: the two sides first, the one to offer next marked; then the bottle; the last feed above them, so the sheet
    /// answers "which side, and how long ago" without going back to Today.
    /// </summary>
    private View FeedSheet()
    {
        var children = new List<View>();
        if (_model.LastFeedLine is { Length: > 0 } last)
            children.Add(Ui.Text(last, "Meta", "Ink2"));
        children.Add(Row(SideButton(BreastSide.Left), SideButton(BreastSide.Right)));
        children.Add(Ui.Text(Strings.Get("Feed.BreastHint"), "Meta", "Ink2"));
        children.Add(WideRow(Coo.Mobile.Presentation.Theme.IconGeometry.Feed, Strings.Get("Feed.Bottle"), _model.OpenBottleCommand, "feeding"));
        children.Add(EarlierLink("feeding"));
        return Panel(Strings.Get("Kind.Feed"), [.. children]);
    }

    /// <summary>A side to start a timer on, in the feed colours; the suggested one outlined and labelled "Next".</summary>
    private View SideButton(BreastSide side)
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For("feeding");
        var suggested = _model.SuggestedSide == side;
        var stack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        stack.Add(Ui.Text(Strings.Get(side == BreastSide.Left ? "Feed.Left" : "Feed.Right"), "BodyStrong", look.Ink).Tap(l => l.HorizontalOptions = LayoutOptions.Center));
        if (suggested)
            stack.Add(Ui.Text(Strings.Get("Feed.Next"), "Meta", look.Ink).Tap(l => l.HorizontalOptions = LayoutOptions.Center));
        var card = Ui.Rounded(stack, look.Tint, 20, new Thickness(12, 14), suggested ? look.Ink : null);
        card.MinimumHeightRequest = 72;
        card.StrokeThickness = suggested ? 2 : 0;
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = _model.StartBreastCommand, CommandParameter = side });
        SemanticProperties.SetDescription(card, suggested ? $"{Strings.Get(side == BreastSide.Left ? "Feed.Left" : "Feed.Right")}, {Strings.Get("Feed.Next")}" : Strings.Get(side == BreastSide.Left ? "Feed.Left" : "Feed.Right"));
        return card;
    }

    /// <summary>A full-width row with the kind's icon, a label and a chevron: a second choice under the big buttons.</summary>
    private static View WideRow(string icon, string text, System.Windows.Input.ICommand command, string kindCode)
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For(kindCode);
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
        grid.Add(Ui.Rounded(new Icon(icon, look.Ink, 18), look.Tint, 10, new Thickness(6)).Tap(b => b.VerticalOptions = LayoutOptions.Center), 0);
        grid.Add(Ui.Text(text, "BodyStrong").Tap(l => l.VerticalOptions = LayoutOptions.Center), 1);
        grid.Add(new Icon(Coo.Mobile.Presentation.Theme.IconGeometry.ChevronRight, "Ink2", 18).Tap(i => i.VerticalOptions = LayoutOptions.Center), 2);
        var row = Ui.Rounded(grid, "Surface", 16, new Thickness(14, 10), "Line");
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = command });
        SemanticProperties.SetDescription(row, text);
        return row;
    }

    /// <summary>Wet or dirty. No "both": a dirty diaper is usually wet too, and nobody can tell (owner, 2026-11-03).</summary>
    private View DiaperSheet() => Panel(
        Strings.Get("Kind.Diaper"),
        Row(
            Big(Strings.Get("Diaper.Wet"), _model.LogDiaperCommand, DiaperContents.Wet, "diaper"),
            Big(Strings.Get("Diaper.Dirty"), _model.LogDiaperCommand, DiaperContents.Dirty, "diaper")),
        EarlierLink("diaper"));

    /// <summary>
    /// Bottle: which milk, how much (one tap on an amount near the last bottle, or − and +), log. The last bottle is
    /// named above, so "same as last time" needs no memory.
    /// </summary>
    private View BottleSheet()
    {
        var children = new List<View>();
        if (_model.LastBottleLine is { Length: > 0 } last)
            children.Add(Ui.Text(last, "Meta", "Ink2"));

        var milk = new Grid { ColumnSpacing = 8 };
        var choices = new[] { (BottleMilk.Formula, "Milk.Formula"), (BottleMilk.Breast, "Milk.Breast"), (BottleMilk.Both, "Milk.Both") };
        for (var i = 0; i < choices.Length; i++)
        {
            var (choice, key) = choices[i];
            var button = new Button { Text = Strings.Get(key), Padding = new Thickness(4, 0) };
            button.SetDynamicResource(VisualElement.StyleProperty, _model.Milk == choice ? "PrimaryButton" : "GhostButton");
            button.Padding = new Thickness(4, 0);
            button.Clicked += async (_, _) =>
            {
                _model.Milk = choice;
                await _sheet.ShowAsync(BottleSheet());
            };
            milk.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            milk.Add(button, i);
        }

        children.Add(milk);
        children.Add(Stepper(() => _model.BottleAmount, _model.ChangeAmountCommand, _model.Milk == BottleMilk.Both ? Strings.Get("Milk.Breast") : null));
        if (_model.Milk != BottleMilk.Both)
            children.Add(QuickAmounts());
        else
            children.Add(Stepper(() => _model.SecondAmount, _model.ChangeSecondAmountCommand, Strings.Get("Milk.Formula")));
        children.Add(Primary(Strings.Get("Action.LogBottle"), _model.LogBottleCommand));
        children.Add(EarlierLink("feeding", bottle: true));
        return Panel(Strings.Get("Feed.Bottle"), [.. children]);
    }

    /// <summary>Amounts one tap away, the chosen one filled.</summary>
    private View QuickAmounts()
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For("feeding");
        var grid = new Grid { ColumnSpacing = 8 };
        var amounts = _model.QuickAmounts;
        for (var i = 0; i < amounts.Count; i++)
        {
            var amount = amounts[i];
            var chosen = amount == _model.BottleAmount;
            var chip = Ui.Rounded(
                Ui.Text($"{amount:0.#}", chosen ? "BodyStrong" : "Body", look.Ink).Tap(l => l.HorizontalOptions = LayoutOptions.Center),
                chosen ? look.Tint : "Surface", 999, new Thickness(4, 8), chosen ? look.Ink : "Line");
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    _model.SetAmountCommand.Execute(amount);
                    await _sheet.ShowAsync(BottleSheet());
                }),
            });
            SemanticProperties.SetDescription(chip, $"{amount:0.#} {_model.UnitLabel}");
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(chip, i);
        }

        return grid;
    }

    private View PumpSheet()
    {
        View Amount(string placeholder, string property)
        {
            var entry = new Entry { Placeholder = $"{placeholder} ({_model.UnitLabel})", Keyboard = Keyboard.Numeric, FontSize = 17, FontFamily = "FigtreeRegular" };
            entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
            entry.BindingContext = _model;
            entry.SetBinding(Entry.TextProperty, property);
            return Ui.Rounded(entry, "Surface", 12, new Thickness(16, 4), "Line");
        }

        var views = new List<View>
        {
            Amount(Strings.Get("Feed.Left"), nameof(TodayViewModel.PumpLeft)),
            Amount(Strings.Get("Feed.Right"), nameof(TodayViewModel.PumpRight)),
        };

        // Into the milk stash at once: a bag with its number, to write on the container.
        if (_model.CanStash)
        {
            views.Add(Ui.Text(Strings.Get("Pump.Stash"), "Meta", "Ink2"));
            var choices = new (string Code, string Key)[] { ("none", "Pump.NotStored"), ("fridge", "Stash.Fridge"), ("freezer", "Stash.Freezer") };
            var buttons = new List<Button>();
            void Restyle()
            {
                for (var i = 0; i < choices.Length; i++)
                {
                    var chosen = choices[i].Code switch
                    {
                        "fridge" => _model.StashPlace == Coo.Domain.CareLog.Kinds.MilkStash.MilkPlace.Fridge,
                        "freezer" => _model.StashPlace == Coo.Domain.CareLog.Kinds.MilkStash.MilkPlace.Freezer,
                        _ => _model.StashPlace is null,
                    };
                    buttons[i].SetDynamicResource(StyleProperty, chosen ? "PrimaryButton" : "GhostButton");
                }
            }

            var row = new Grid { ColumnSpacing = 8 };
            foreach (var (code, key) in choices)
            {
                var button = new Button { Text = Strings.Get(key), Padding = new Thickness(4, 0) };
                button.Clicked += (_, _) =>
                {
                    _model.ChooseStashCommand.Execute(code);
                    Restyle();
                };
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                row.Add(button, buttons.Count);
                buttons.Add(button);
            }

            Restyle();
            views.Add(row);
        }

        views.Add(Row(Ghost(Strings.Get("Action.Skip"), _model.SavePumpYieldCommand, true), Primary(Strings.Get("Action.Save"), _model.SavePumpYieldCommand, false)));
        return Panel(Strings.Get("Pump.HowMuch"), [.. views]);
    }

    /// <summary>"Zosia is asleep · since 20:05": woke up, or still asleep (Spec 006 §5a). Tap outside cancels.</summary>
    private View AwakeSheet() => Panel(
        _model.AwakeTitle,
        Ui.Text(_model.AwakeSince, "Body", "Ink2"),
        Row(Ghost(Strings.Get("Awake.StillAsleep"), _model.ResolveAwakeCommand, false), Primary(Strings.Get("Awake.WokeUp"), _model.ResolveAwakeCommand, true)),
        Ui.Text(Strings.Get("Awake.Hint"), "Meta"));

    private View BirthSheet()
    {
        var picker = new DatePicker { MaximumDate = _model.Today, Date = _model.BirthDate, FontSize = 17, FontFamily = "FigtreeRegular", Format = "d MMM yyyy" };
        picker.SetDynamicResource(DatePicker.TextColorProperty, "Ink");
        picker.DateSelected += (_, e) => _model.BirthDate = e.NewDate ?? _model.Today;
        return Panel(Strings.Get("Due.Arrived"), Ui.Text(Strings.Get("Setup.BirthDate"), "Label"), Ui.Rounded(picker, "Surface", 12, new Thickness(16, 4), "Line"), Primary(Strings.Get("Action.Save"), _model.SaveBirthCommand));
    }

    private View Stepper(Func<decimal> value, System.Windows.Input.ICommand change, string? label)
    {
        var number = Ui.Text($"{value():0.#} {_model.UnitLabel}", "Window");
        number.HorizontalOptions = LayoutOptions.Center;
        number.VerticalOptions = LayoutOptions.Center;
        Button Sign(string sign)
        {
            var b = new Button { Text = sign == "+" ? "+" : "−", WidthRequest = 64, FontSize = 24 };
            b.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
            b.Clicked += async (_, _) =>
            {
                change.Execute(sign);
                number.Text = $"{value():0.#} {_model.UnitLabel}";
                // The quick amounts show which one is chosen: the bottle sheet is drawn again.
                if (_model.Sheet == HomeSheet.Bottle && _model.Milk != BottleMilk.Both)
                    await _sheet.ShowAsync(BottleSheet());
            };
            return b;
        }

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        grid.Add(Sign("-"), 0);
        grid.Add(number, 1);
        grid.Add(Sign("+"), 2);
        return label is null ? grid : new VerticalStackLayout { Spacing = 4, Children = { Ui.Text(label, "Label"), grid } };
    }

    private static VerticalStackLayout Panel(string title, params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 14 };
        stack.Add(Ui.Text(title, "Section"));
        foreach (var child in children)
            stack.Add(child);
        return stack;
    }

    private static Grid Row(params View[] views)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < views.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(views[i], i);
        }

        return grid;
    }

    private static Button Big(string text, System.Windows.Input.ICommand command, object parameter, string kindCode)
    {
        var look = Coo.Mobile.Presentation.Theme.EventAppearance.For(kindCode);
        var button = new Button { Text = text, Command = command, CommandParameter = parameter, MinimumHeightRequest = 68, CornerRadius = 20, FontFamily = "FigtreeSemiBold", FontSize = 17 };
        button.SetDynamicResource(VisualElement.BackgroundColorProperty, look.Tint);
        button.SetDynamicResource(Button.TextColorProperty, look.Ink);
        return button;
    }

    private static Button Primary(string text, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter };
        button.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        return button;
    }

    private static Button Ghost(string text, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = new Button { Text = text, Command = command, CommandParameter = parameter };
        button.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        return button;
    }

    /// <summary>Plain messages ("Switch side") go through the one toast; logs with Undo arrive from the service.</summary>
    private void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _undo.Say(text);
        _model.Toast = "";
    }
}
