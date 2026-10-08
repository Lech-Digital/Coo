using Coo.Mobile.Components;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.AppVersion;

public sealed class UpdateSheet : Sheet
{
    public UpdateSheet(bool isForced)
    {
        IsDismissable = !isForced;
        
        var message = isForced 
            ? "A critical update is required to continue using Coo. Please update the app now."
            : "A new version of Coo is available. Update now to get the latest features and bug fixes.";

        var button = new Button { Text = "Update Now" };
        button.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        button.Clicked += async (_, _) =>
        {
            try {
                await Launcher.OpenAsync("market://details?id=com.lechdigital.coo");
            } catch {
                await Launcher.OpenAsync("https://play.google.com/store/apps/details?id=com.lechdigital.coo");
            }
        };

        var c = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            RowSpacing = 24,
            Children =
            {
                Ui.Text(message, Ui.TextRole.Body)
                    .Row(0)
                    .AlignStart()
                    .Colour("Ink2"),
                
                button.Row(1).AlignEnd()
            }
        };

        Content = new ContentView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 24,
                Children =
                {
                    Ui.Text("Update Available", Ui.TextRole.Title3).Colour("Ink"),
                    c
                }
            }
        };
    }
}
