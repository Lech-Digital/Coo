using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Advisor;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Coo.Mobile.Pages;

public sealed class AdvisorPage : ContentPage
{
    private readonly AdvisorViewModel _model;

    public AdvisorPage(AdvisorViewModel model)
    {
        _model = model;
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        SetupUi();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.InitializeAsync();
    }

    private void SetupUi()
    {
        // Header
        var titleLabel = new Label
        {
            Text = "Baby Advisor",
            FontFamily = "FigtreeBold",
            FontSize = 24,
            VerticalOptions = LayoutOptions.Center
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "Ink");

        var newChatButton = new Button
        {
            Text = "New",
            FontFamily = "FigtreeBold",
            FontSize = 14,
            Padding = new Thickness(12, 4),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End
        };
        newChatButton.SetDynamicResource(Button.TextColorProperty, "Accent");
        newChatButton.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");
        newChatButton.SetBinding(Button.CommandProperty, "ResetChatCommand");

        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star) },
            Margin = new Thickness(0, 0, 0, 8)
        };
        header.Add(titleLabel, 0);

        var subtitle = new Label
        {
            Text = "Powered by Google Gemini. Ask questions about your baby's development, sleep, or feeding.",
            FontFamily = "FigtreeRegular",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 16)
        };
        subtitle.SetDynamicResource(Label.TextColorProperty, "Ink2");

        var devLabel = new Label { FontFamily = "FigtreeSemiBold", FontSize = 16 };
        devLabel.SetBinding(Label.TextProperty, "DevelopmentTitle");
        devLabel.SetDynamicResource(Label.TextColorProperty, "Ink");
        
        var devText = new Label { FontFamily = "FigtreeRegular", FontSize = 14 };
        devText.SetBinding(Label.TextProperty, "DevelopmentText");
        devText.SetDynamicResource(Label.TextColorProperty, "Ink");

        // Development Milestones Placeholder
        var milestonesFrame = Ui.Rounded(
            new VerticalStackLayout
            {
                Spacing = 8,
                Children = { devLabel, devText }
            },
            "Surface", 16, new Thickness(16), "Line");

        // Suggested Questions "Pills"
        var pillsLayout = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            Direction = FlexDirection.Row,
            JustifyContent = FlexJustify.Start,
            Margin = new Thickness(0, 16, 0, 8)
        };
        
        BindableLayout.SetItemsSource(pillsLayout, _model.SuggestedQuestions);
        BindableLayout.SetItemTemplate(pillsLayout, new DataTemplate(() =>
        {
            var pillLabel = new Label
            {
                FontFamily = "FigtreeMedium",
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center
            };
            pillLabel.SetBinding(Label.TextProperty, ".");
            pillLabel.SetDynamicResource(Label.TextColorProperty, "Accent");

            var pill = Ui.Rounded(pillLabel, "Surface", 16, new Thickness(12, 8), "Accent");
            pill.Margin = new Thickness(0, 0, 8, 8);
            
            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) =>
            {
                if (pillLabel.Text != null)
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

        // Chat Messages
        var messagesCollection = new CollectionView
        {
            Margin = new Thickness(0, 16, 0, 0),
            ItemTemplate = new DataTemplate(() =>
            {
                var textLabel = new Label
                {
                    FontFamily = "FigtreeRegular",
                    FontSize = 14,
                };
                textLabel.SetBinding(Label.TextProperty, "Text");
                textLabel.SetDynamicResource(Label.TextColorProperty, "Ink");

                var bubble = Ui.Rounded(textLabel, "Surface", 12, new Thickness(12, 8), "Line");
                bubble.SetBinding(View.HorizontalOptionsProperty, new Binding("IsUser", converter: new BoolToLayoutOptionsConverter()));
                
                var container = new ContentView
                {
                    Content = bubble,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                return container;
            })
        };
        messagesCollection.SetBinding(ItemsView.ItemsSourceProperty, "Messages");

        // Input Area
        var inputEntry = new Entry
        {
            Placeholder = "Ask about your baby...",
            FontFamily = "FigtreeRegular",
            FontSize = 16,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center
        };
        inputEntry.SetBinding(Entry.TextProperty, "Question");
        inputEntry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        inputEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");

        var sendButton = new Button
        {
            Text = "Ask",
            FontFamily = "FigtreeBold",
            FontSize = 14,
            CornerRadius = 12,
            HeightRequest = 44,
            VerticalOptions = LayoutOptions.Center
        };
        sendButton.SetBinding(Button.CommandProperty, "AskCommand");
        sendButton.SetDynamicResource(Button.TextColorProperty, "Surface");
        sendButton.SetDynamicResource(VisualElement.BackgroundColorProperty, "Accent");

        var activityIndicator = new ActivityIndicator
        {
            VerticalOptions = LayoutOptions.Center
        };
        activityIndicator.SetBinding(ActivityIndicator.IsRunningProperty, "IsBusy");
        activityIndicator.SetBinding(ActivityIndicator.IsVisibleProperty, "IsBusy");
        activityIndicator.SetDynamicResource(ActivityIndicator.ColorProperty, "Accent");
        
        sendButton.SetBinding(VisualElement.IsVisibleProperty, new Binding("IsBusy", converter: new InverseBoolConverter()));

        var inputRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        
        var inputRounded = Ui.Rounded(inputEntry, "Surface", 12, new Thickness(16, 4), "Line");

        inputRow.Add(inputRounded, 0);
        
        var buttonContainer = new HorizontalStackLayout { Children = { sendButton, activityIndicator } };
        inputRow.Add(buttonContainer, 1);

        pillsLayout.SetBinding(VisualElement.IsVisibleProperty, new Binding("HasStartedChat", converter: new InverseBoolConverter()));

        var topLayout = new VerticalStackLayout
        {
            Padding = new Thickness(24, 64, 24, 24),
            Children = { header, subtitle, milestonesFrame, pillsLayout }
        };

        messagesCollection.Header = topLayout;

        var mainLayout = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            RowSpacing = 0
        };

        mainLayout.Add(messagesCollection, 0, 0);
        
        newChatButton.SetBinding(VisualElement.IsVisibleProperty, "HasStartedChat");
        newChatButton.HorizontalOptions = LayoutOptions.Center;
        
        var bottomLayout = new ContentView
        {
            Padding = new Thickness(24, 0, 24, 24),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children = { newChatButton, inputRow }
            }
        };
        mainLayout.Add(bottomLayout, 0, 1);

        Content = mainLayout;
        this.SetDynamicResource(VisualElement.BackgroundColorProperty, "Ground");
    }
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
