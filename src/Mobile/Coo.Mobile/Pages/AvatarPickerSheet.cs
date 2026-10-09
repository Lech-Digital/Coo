using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Pages;

/// <summary>
/// A dedicated visual avatar selector bottom sheet/popup screen showing baby face avatars
/// in a clean 4x2 grid with high-resolution visual previews.
/// </summary>
public sealed class AvatarPickerSheet : Grid
{
    private readonly Border _panel;
    private readonly BoxView _backdrop;
    private readonly BabyProfileViewModel _model;
    private Action? _onDismiss;

    public AvatarPickerSheet(BabyProfileViewModel model)
    {
        _model = model;
        IsVisible = false;

        _backdrop = new BoxView { Color = Colors.Black, Opacity = 0 };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await HideAsync();
        _backdrop.GestureRecognizers.Add(tap);

        _panel = new Border
        {
            VerticalOptions = LayoutOptions.End,
            StrokeThickness = 0,
            Padding = new Thickness(24, 20, 24, 34),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28, 28, 0, 0) },
        };
        _panel.SetDynamicResource(VisualElement.BackgroundColorProperty, "Surface");

        Children.Add(_backdrop);
        Children.Add(_panel);
    }

    public async Task ShowAsync(Action? onDismiss = null)
    {
        _onDismiss = onDismiss;
        _panel.Content = BuildContent();

        if (IsVisible)
            return;

        IsVisible = true;
        _panel.TranslationY = 500;
        await Task.WhenAll(_backdrop.FadeToAsync(0.4, 180), _panel.TranslateToAsync(0, 0, 200, Easing.CubicOut));
    }

    public async Task HideAsync()
    {
        if (!IsVisible)
            return;

        await Task.WhenAll(_backdrop.FadeToAsync(0, 150), _panel.TranslateToAsync(0, 500, 180, Easing.CubicIn));
        IsVisible = false;
        _panel.Content = null;
        _onDismiss?.Invoke();
    }

    private View BuildContent()
    {
        // Drag handle indicator
        var handle = new BoxView
        {
            WidthRequest = 40,
            HeightRequest = 4,
            CornerRadius = 2,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        handle.SetDynamicResource(BoxView.ColorProperty, "Line");

        var title = Ui.Text("Choose Baby Avatar", "H2").Tap(l =>
        {
            l.HorizontalOptions = LayoutOptions.Center;
            l.Margin = new Thickness(0, 0, 0, 4);
        });

        var subtitle = Ui.Text("Pick an illustrated avatar for your baby's profile", "Body", "Ink2").Tap(l =>
        {
            l.HorizontalOptions = LayoutOptions.Center;
            l.Margin = new Thickness(0, 0, 0, 20);
        });

        // 4 columns grid for the 8 presets
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star },
            },
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            },
            RowSpacing = 18,
            ColumnSpacing = 12,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var currentPresetKey = _model.AvatarRef?.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) == true
            ? _model.AvatarRef["preset:".Length..]
            : null;

        for (int i = 0; i < CooAvatar.Presets.Count; i++)
        {
            var preset = CooAvatar.Presets[i];
            var row = i / 4;
            var col = i % 4;

            var isSelected = string.Equals(currentPresetKey, preset.Key, StringComparison.OrdinalIgnoreCase);

            var avatarImage = new Image
            {
                Source = preset.ImageResource,
                Aspect = Aspect.AspectFill,
                WidthRequest = 64,
                HeightRequest = 64,
            };

            var avatarCircle = new Border
            {
                Content = avatarImage,
                Padding = 0,
                WidthRequest = 64,
                HeightRequest = 64,
                StrokeShape = new RoundRectangle { CornerRadius = 32 },
                StrokeThickness = isSelected ? 3 : 1,
            };

            if (isSelected)
            {
                avatarCircle.SetDynamicResource(Border.StrokeProperty, "AccentBrush");
            }
            else
            {
                avatarCircle.SetDynamicResource(Border.StrokeProperty, "LineBrush");
            }

            var itemStack = new VerticalStackLayout
            {
                Spacing = 6,
                HorizontalOptions = LayoutOptions.Center,
                Children = { avatarCircle }
            };

            var itemTap = new TapGestureRecognizer();
            itemTap.Tapped += async (_, _) =>
            {
                await _model.SetPresetAvatarCommand.ExecuteAsync(preset.Key);
                await HideAsync();
            };
            itemStack.GestureRecognizers.Add(itemTap);

            grid.Add(itemStack, col, row);
        }

        var closeButton = new Button
        {
            Text = Strings.Get("Action.Cancel"),
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalOptions = LayoutOptions.Fill,
        };
        closeButton.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");
        closeButton.Clicked += async (_, _) => await HideAsync();

        return new VerticalStackLayout
        {
            Children =
            {
                handle,
                title,
                subtitle,
                grid,
                closeButton,
            }
        };
    }
}
