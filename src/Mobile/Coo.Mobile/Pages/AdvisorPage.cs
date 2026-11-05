using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Billing;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Advisor;
using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Coo.Mobile.Pages;

public sealed class AdvisorPage : ContentPage
{
    private readonly AdvisorViewModel _model;
    private readonly IServiceProvider _services;
    private CooPageHeader? _pageHeader;

    public AdvisorPage(AdvisorViewModel model, IServiceProvider services)
    {
        _model = model;
        _model.EnsureConsentAsync = EnsureConsentAsync;
        _services = services;
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        SetupUi();
    }

    private const string ConsentKey = "advisor.consent.v2";

    /// <summary>The Advisor's consent (Advisor.ConsentBody), asked before the first question and remembered on this phone.</summary>
    private Task<bool> EnsureConsentAsync() => AskConsentAsync(this);

    /// <summary>Whether the Advisor's consent was given on this phone.</summary>
    internal static bool HasConsent => Preferences.Default.Get(ConsentKey, false);

    /// <summary>The same consent from any page that sends the log to the Advisor (Stats' weekly summary too).</summary>
    internal static async Task<bool> AskConsentAsync(Page page)
    {
        if (Preferences.Default.Get(ConsentKey, false))
            return true;
        var agreed = await Components.CooDialog.ConfirmAsync(page, Strings.Get("Advisor.ConsentTitle"), Strings.Get("Advisor.ConsentBody"), Strings.Get("Advisor.ConsentAgree"), Strings.Get("Advisor.ConsentLater"));
        if (agreed)
            Preferences.Default.Set(ConsentKey, true);
        return agreed;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _pageHeader?.Refresh();
        await _model.InitializeAsync();
    }

    private void SetupUi()
    {
        // 1. Shared standard Page Header
        _pageHeader = new CooPageHeader(_services);
        _pageHeader.BabyChanged += () => Dispatcher.Dispatch(async () => await _model.InitializeAsync());

        // Subheader action bar for Advisor (History drawer button + New chat button)
        var historyIcon = new Label
        {
            Text = Strings.Get("Advisor.History"),
            FontFamily = "FigtreeMedium",
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center
        };
        historyIcon.SetDynamicResource(Label.TextColorProperty, "Ink");

        var historyButton = Ui.Rounded(historyIcon, "Glass", 999, new Thickness(12, 6), "GlassLine");
        historyButton.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => _model.ToggleHistoryCommand.Execute(null))
        });

        var newChatIcon = new Label
        {
            Text = "+ " + Strings.Get("Advisor.New"),
            FontFamily = "FigtreeBold",
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center
        };
        newChatIcon.SetDynamicResource(Label.TextColorProperty, "Accent");

        var newChatButton = Ui.Rounded(newChatIcon, "Glass", 999, new Thickness(14, 6), "GlassLine");
        newChatButton.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => _model.ResetChatCommand.Execute(null))
        });

        var subheaderBar = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { historyButton, newChatButton }
        };
        Grid.SetColumn(newChatButton, 1);

        // 2. Subtitle
        var titleLabel = new Label
        {
            FontFamily = "FigtreeSemiBold",
            FontSize = 28,
            CharacterSpacing = -0.4,
            Margin = new Thickness(0, 0, 0, 2)
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "Ink");
        titleLabel.SetBinding(Label.TextProperty, new Binding("HasStartedChat", converter: new BoolToStringConverter(Strings.Get("Advisor.Conversation"), Strings.Get("Advisor.Title"))));

        var subtitleLabel = new Label
        {
            Text = Strings.Get("Advisor.Subtitle"),
            FontFamily = "FigtreeRegular",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12)
        };
        subtitleLabel.SetDynamicResource(Label.TextColorProperty, "Ink2");

        // 3. Milestones Card
        var devLabel = new Label { FontFamily = "FigtreeBold", FontSize = 15 };
        devLabel.SetBinding(Label.TextProperty, "DevelopmentTitle");
        devLabel.SetDynamicResource(Label.TextColorProperty, "Ink");

        var devSummary = new Label { FontFamily = "FigtreeRegular", FontSize = 14, LineHeight = 1.2 };
        devSummary.SetBinding(Label.TextProperty, "DevelopmentSummary");
        devSummary.SetDynamicResource(Label.TextColorProperty, "Ink");

        var devFull = new Label { FontFamily = "FigtreeRegular", FontSize = 13, LineHeight = 1.3, Margin = new Thickness(0, 8, 0, 0) };
        devFull.SetBinding(Label.TextProperty, "DevelopmentFullGuide");
        devFull.SetBinding(VisualElement.IsVisibleProperty, "IsDevelopmentExpanded");
        devFull.SetDynamicResource(Label.TextColorProperty, "Ink2");

        var toggleGuideButton = new Button
        {
            FontFamily = "FigtreeSemiBold",
            FontSize = 13,
            Padding = new Thickness(8, 2),
            HorizontalOptions = LayoutOptions.Start,
            BackgroundColor = Colors.Transparent,
            Margin = new Thickness(-8, 4, 0, -4)
        };
        toggleGuideButton.SetDynamicResource(Button.TextColorProperty, "Accent");
        toggleGuideButton.SetBinding(Button.TextProperty, new Binding("IsDevelopmentExpanded", converter: new BoolToStringConverter(Strings.Get("Advisor.HideGuide"), Strings.Get("Advisor.ShowGuide"))));
        toggleGuideButton.SetBinding(Button.CommandProperty, "ToggleDevelopmentExpandedCommand");

        var milestonesCard = Ui.Rounded(
            new VerticalStackLayout
            {
                Spacing = 6,
                Children = { devLabel, devSummary, devFull, toggleGuideButton }
            },
            "Glass", 20, new Thickness(16), "GlassLine");
        milestonesCard.SetBinding(VisualElement.IsVisibleProperty, new Binding("HasStartedChat", converter: new InverseBoolConverter()));

        // 4. Suggested Questions "Pills"
        var pillsLayout = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            Direction = FlexDirection.Row,
            JustifyContent = FlexJustify.Start,
            Margin = new Thickness(0, 12, 0, 8)
        };

        BindableLayout.SetItemsSource(pillsLayout, _model.SuggestedQuestions);
        BindableLayout.SetItemTemplate(pillsLayout, new DataTemplate(() =>
        {
            var pillLabel = new Label
            {
                FontFamily = "FigtreeMedium",
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                // A long question wraps instead of being cut off at the screen's edge (found on the Pixel 10).
                LineBreakMode = LineBreakMode.WordWrap,
            };
            pillLabel.SetBinding(Label.TextProperty, ".");
            pillLabel.SetDynamicResource(Label.TextColorProperty, "Accent");

            var pill = Ui.Rounded(pillLabel, "Glass", 16, new Thickness(12, 8), "GlassLine");
            // A flex item is measured unconstrained, so a long question ran off the screen: cap it at the page's width.
            pill.MaximumWidthRequest = (DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density) - 56;
            pill.Margin = new Thickness(0, 0, 6, 8);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) =>
            {
                if (pillLabel.Text != null && !_model.IsBusy)
                {
                    _model.Question = pillLabel.Text;
                    if (_model.AskCommand.CanExecute(null))
                    {
                        _model.AskCommand.Execute(null);
                    }
                }
            };
            pill.GestureRecognizers.Add(tap);
            return pill;
        }));

        var shuffleLabel = new Label
        {
            Text = Strings.Get("Advisor.MoreQuestions"),
            FontFamily = "FigtreeBold",
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center
        };
        shuffleLabel.SetDynamicResource(Label.TextColorProperty, "AccentInk");

        var shufflePill = Ui.Rounded(shuffleLabel, "AccentSoft", 16, new Thickness(12, 8), "Accent");
        shufflePill.Margin = new Thickness(0, 0, 6, 8);
        var shuffleTap = new TapGestureRecognizer();
        shuffleTap.Tapped += (_, _) => _model.ShuffleQuestionsCommand.Execute(null);
        shufflePill.GestureRecognizers.Add(shuffleTap);

        var pillsContainer = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { pillsLayout, shufflePill }
        };
        pillsContainer.SetBinding(VisualElement.IsVisibleProperty, new Binding("HasStartedChat", converter: new InverseBoolConverter()));

        // 5. Chat Messages List
        var messagesCollection = new CollectionView
        {
            Margin = new Thickness(0, 8, 0, 0),
            ItemTemplate = new DataTemplate(() =>
            {
                var textLabel = new Label
                {
                    FontFamily = "FigtreeRegular",
                    FontSize = 14,
                    LineHeight = 1.25
                };
                textLabel.SetBinding(Label.TextProperty, "Text");

                var bubble = new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 14 },
                    Padding = new Thickness(14, 10),
                    Content = textLabel
                };

                bubble.SetBinding(View.HorizontalOptionsProperty, new Binding("IsUser", converter: new BoolToLayoutOptionsConverter()));
                bubble.SetBinding(VisualElement.BackgroundColorProperty, new Binding("IsUser", converter: new BoolToDynamicResourceConverter("AccentSoft", "Glass")));
                bubble.SetBinding(Border.StrokeProperty, new Binding("IsUser", converter: new BoolToDynamicResourceConverter("Accent", "GlassLine")));
                textLabel.SetBinding(Label.TextColorProperty, new Binding("IsUser", converter: new BoolToDynamicResourceConverter("AccentInk", "Ink")));

                return new ContentView
                {
                    Content = bubble,
                    Margin = new Thickness(0, 0, 0, 10)
                };
            })
        };
        messagesCollection.SetBinding(ItemsView.ItemsSourceProperty, "Messages");
        messagesCollection.SetBinding(VisualElement.IsVisibleProperty, "HasStartedChat");

        // 6. History Drawer / Overlay
        var historyCloseBtn = new Button
        {
            Text = Strings.Get("Advisor.Done"),
            FontFamily = "FigtreeBold",
            FontSize = 13,
            Padding = new Thickness(12, 4),
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.End
        };
        historyCloseBtn.SetDynamicResource(Button.TextColorProperty, "Accent");
        historyCloseBtn.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        historyCloseBtn.SetBinding(Button.CommandProperty, "ToggleHistoryCommand");

        var historyTitle = new Label
        {
            Text = Strings.Get("Advisor.HistoryTitle"),
            FontFamily = "FigtreeBold",
            FontSize = 18,
            VerticalOptions = LayoutOptions.Center
        };
        historyTitle.SetDynamicResource(Label.TextColorProperty, "Ink");

        var historyHeader = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Margin = new Thickness(0, 0, 0, 12),
            Children = { historyTitle, historyCloseBtn }
        };
        Grid.SetColumn(historyCloseBtn, 1);

        var startNewChatBtn = new Button
        {
            Text = "+ " + Strings.Get("Advisor.StartNew"),
            FontFamily = "FigtreeBold",
            FontSize = 14,
            CornerRadius = 12,
            HeightRequest = 44,
            Margin = new Thickness(0, 0, 0, 12)
        };
        startNewChatBtn.SetDynamicResource(Button.TextColorProperty, "Surface");
        startNewChatBtn.SetDynamicResource(VisualElement.BackgroundColorProperty, "Accent");
        startNewChatBtn.SetBinding(Button.CommandProperty, "ResetChatCommand");

        var threadsCollection = new CollectionView
        {
            ItemTemplate = new DataTemplate(() =>
            {
                var title = new Label { FontFamily = "FigtreeSemiBold", FontSize = 14 };
                title.SetBinding(Label.TextProperty, "Title");
                title.SetDynamicResource(Label.TextColorProperty, "Ink");

                var meta = new Label { FontFamily = "FigtreeRegular", FontSize = 12 };
                meta.SetBinding(Label.TextProperty, new Binding("LastUpdatedAt", stringFormat: "{0:MMM d, HH:mm}"));
                meta.SetDynamicResource(Label.TextColorProperty, "Ink2");

                var infoStack = new VerticalStackLayout { Spacing = 2, Children = { title, meta } };

                var deleteBtn = new Button
                {
                    Text = "✕",
                    FontFamily = "FigtreeBold",
                    FontSize = 14,
                    WidthRequest = 36,
                    HeightRequest = 36,
                    CornerRadius = 18,
                    Padding = 0,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.End
                };
                deleteBtn.SetDynamicResource(Button.TextColorProperty, "Ink2");
                deleteBtn.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
                deleteBtn.SetBinding(Button.CommandParameterProperty, ".");
                deleteBtn.SetBinding(Button.CommandProperty, new Binding("BindingContext.DeleteThreadCommand", source: this));

                var rowGrid = new Grid
                {
                    ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                    Padding = new Thickness(14, 12),
                    Children = { infoStack, deleteBtn }
                };
                Grid.SetColumn(deleteBtn, 1);

                var card = Ui.Rounded(rowGrid, "Glass", 12, 0, "GlassLine");
                card.Margin = new Thickness(0, 0, 0, 8);

                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) =>
                {
                    if (card.BindingContext is AdvisorThreadSummary summary)
                    {
                        _model.OpenThreadCommand.Execute(summary);
                    }
                };
                card.GestureRecognizers.Add(tap);

                return card;
            })
        };
        threadsCollection.SetBinding(ItemsView.ItemsSourceProperty, "Threads");

        var emptyHistoryLabel = new Label
        {
            Text = Strings.Get("Advisor.NoHistory"),
            FontFamily = "FigtreeRegular",
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 32, 0, 0)
        };
        emptyHistoryLabel.SetDynamicResource(Label.TextColorProperty, "Ink2");
        emptyHistoryLabel.SetBinding(VisualElement.IsVisibleProperty, new Binding("Threads.Count", converter: new ZeroToBoolConverter()));

        var historyLayout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            Children = { historyHeader, startNewChatBtn, new Grid { Children = { threadsCollection, emptyHistoryLabel } } }
        };
        Grid.SetRow(startNewChatBtn, 1);
        Grid.SetRow(threadsCollection.Parent as View ?? historyLayout.Children[2] as View, 2);
        historyLayout.SetBinding(VisualElement.IsVisibleProperty, "IsHistoryOpen");

        // 7. Bottom Input Area
        var inputEntry = new Entry
        {
            Placeholder = Strings.Get("Advisor.Placeholder"),
            FontFamily = "FigtreeRegular",
            FontSize = 15,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center
        };
        inputEntry.SetBinding(Entry.TextProperty, "Question");
        inputEntry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        inputEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");

        var sendButton = new Button
        {
            Text = Strings.Get("Advisor.Ask"),
            FontFamily = "FigtreeBold",
            FontSize = 14,
            CornerRadius = 12,
            HeightRequest = 44,
            Padding = new Thickness(16, 0),
            VerticalOptions = LayoutOptions.Center
        };
        sendButton.SetBinding(Button.CommandProperty, "AskCommand");
        sendButton.SetDynamicResource(Button.TextColorProperty, "Surface");
        sendButton.SetDynamicResource(VisualElement.BackgroundColorProperty, "Accent");

        var activityIndicator = new ActivityIndicator
        {
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 36,
            HeightRequest = 36
        };
        activityIndicator.SetBinding(ActivityIndicator.IsRunningProperty, "IsBusy");
        activityIndicator.SetBinding(ActivityIndicator.IsVisibleProperty, "IsBusy");
        activityIndicator.SetDynamicResource(ActivityIndicator.ColorProperty, "Accent");

        sendButton.SetBinding(VisualElement.IsVisibleProperty, new Binding("IsBusy", converter: new InverseBoolConverter()));

        var inputRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8,
            Margin = new Thickness(0, 10, 0, 0)
        };

        if (_model.CanSpeak)
            inputRow.Add(MicButton(), 0);
        var inputRounded = Ui.Rounded(inputEntry, "Glass", 12, new Thickness(14, 2), "GlassLine");
        inputRow.Add(inputRounded, 1);

        var buttonContainer = new HorizontalStackLayout { Children = { sendButton, activityIndicator } };
        inputRow.Add(buttonContainer, 2);

        // Always in view under the question box (Play's health-content policy, and plain honesty).
        var disclaimer = Ui.Text(Strings.Get("Advisor.Disclaimer"), "Meta", "Ink2").Tap(l => l.FontSize = 11);
        var voiceMessage = Ui.Text("", "Meta", "AccentInk");
        voiceMessage.SetBinding(Label.TextProperty, nameof(AdvisorViewModel.VoiceMessage));
        voiceMessage.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(AdvisorViewModel.VoiceMessage), converter: new NotEmptyConverter()));
        var bottomContainer = new ContentView
        {
            Content = new VerticalStackLayout { Spacing = 4, Children = { inputRow, voiceMessage, disclaimer } }
        };
        bottomContainer.SetBinding(VisualElement.IsVisibleProperty, new Binding("IsHistoryOpen", converter: new InverseBoolConverter()));

        // 5. Welcome View (when !HasStartedChat)
        var welcomeContent = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { titleLabel, subtitleLabel, milestonesCard, pillsContainer }
        };
        var welcomeScrollView = new ScrollView
        {
            Content = welcomeContent
        };
        welcomeScrollView.SetBinding(VisualElement.IsVisibleProperty, new Binding("HasStartedChat", converter: new InverseBoolConverter()));

        // Content Scrollable Header for active chat (only title & subtitle when chatting)
        var chatTitleLabel = new Label
        {
            Text = Strings.Get("Advisor.Conversation"),
            FontFamily = "FigtreeSemiBold",
            FontSize = 28,
            CharacterSpacing = -0.4,
            Margin = new Thickness(0, 0, 0, 2)
        };
        chatTitleLabel.SetDynamicResource(Label.TextColorProperty, "Ink");

        var chatSubtitleLabel = new Label
        {
            Text = Strings.Get("Advisor.Subtitle"),
            FontFamily = "FigtreeRegular",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12)
        };
        chatSubtitleLabel.SetDynamicResource(Label.TextColorProperty, "Ink2");

        var chatHeader = new VerticalStackLayout
        {
            Children = { chatTitleLabel, chatSubtitleLabel }
        };
        messagesCollection.Header = chatHeader;

        // Main Layout
        var chatLayout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            RowSpacing = 0
        };
        chatLayout.Add(_pageHeader!, 0, 0);

        var contentContainer = new Grid
        {
            Padding = new Thickness(24, 0, 24, 0),
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { subheaderBar, welcomeScrollView, messagesCollection, bottomContainer }
        };
        Grid.SetRow(subheaderBar, 0);
        Grid.SetRow(welcomeScrollView, 1);
        Grid.SetRow(messagesCollection, 1);
        Grid.SetRow(bottomContainer, 2);

        chatLayout.Add(contentContainer, 0, 1);
        chatLayout.SetBinding(VisualElement.IsVisibleProperty, new Binding("IsHistoryOpen", converter: new InverseBoolConverter()));

        var historyContainer = new ContentView
        {
            Padding = new Thickness(24, 16, 24, 20),
            Content = historyLayout
        };
        historyContainer.SetBinding(VisualElement.IsVisibleProperty, "IsHistoryOpen");

        var rootGrid = new Grid
        {
            Padding = new Thickness(0, 0, 0, 20),
            Children = { chatLayout, historyContainer }
        };

        Content = Backdrop.Behind(rootGrid);
        this.SetDynamicResource(VisualElement.BackgroundColorProperty, "Ground");
    }

    private long _micPressedAt;
    private bool _micStartedByPress;

    /// <summary>
    /// The mic beside the question box, as in Tell Coo: hold to talk and let go, or tap, talk, and tap again (or pause).
    /// Accent while recording.
    /// </summary>
    private Grid MicButton()
    {
        var face = new Border
        {
            WidthRequest = 44,
            HeightRequest = 44,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            Content = new Coo.Mobile.Components.Icon(Coo.Mobile.Presentation.Theme.IconGeometry.Mic, "OnAccent", 22) { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            InputTransparent = true,
        };
        void Paint() => face.SetDynamicResource(VisualElement.BackgroundColorProperty, _model.IsRecording ? "AccentInk" : "Accent");
        Paint();
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdvisorViewModel.IsRecording))
                Dispatcher.Dispatch(Paint);
        };
        var press = new Button { WidthRequest = 44, HeightRequest = 44, CornerRadius = 22, BackgroundColor = Colors.Transparent, BorderWidth = 0 };
        SemanticProperties.SetDescription(press, Strings.Get("Quick.HoldHint"));
        press.Pressed += async (_, _) =>
        {
            if (_model.IsRecording)
            {
                _micStartedByPress = false;
                _model.FinishSpeakingCommand.Execute(null);
                return;
            }

            // The first time: Coo says what the microphone is for before Android asks; that press becomes a tap.
            if (await Permissions.CheckStatusAsync<Permissions.Microphone>() != PermissionStatus.Granted
                && !await Components.CooDialog.PrimeAsync<Permissions.Microphone>(this, "Permission.MicTitle", "Permission.MicBody"))
                return;

            _micPressedAt = Environment.TickCount64;
            _micStartedByPress = true;
            _model.SpeakCommand.Execute(null);
        };
        press.Released += (_, _) =>
        {
            if (_micStartedByPress && Environment.TickCount64 - _micPressedAt >= 600)
                _model.FinishSpeakingCommand.Execute(null);
            _micStartedByPress = false;
        };
        return new Grid { VerticalOptions = LayoutOptions.Center, Children = { face, press } };
    }
}

public class BoolToStringConverter(string trueValue, string falseValue) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is bool b && b ? trueValue : falseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class ZeroToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToLayoutOptionsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isUser && isUser)
        {
            return LayoutOptions.End;
        }
        return LayoutOptions.Start;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToDynamicResourceConverter(string trueKey, string falseKey) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = value is bool b && b ? trueKey : falseKey;
        if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var resource) == true)
        {
            if (targetType == typeof(Brush) && resource is Color c)
                return new SolidColorBrush(c);
            return resource;
        }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>True for a non-empty string: a message line shows only when there is something to say.</summary>
public class NotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is string { Length: > 0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
