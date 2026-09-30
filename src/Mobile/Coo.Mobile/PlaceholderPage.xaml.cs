namespace Coo.Mobile;

public partial class PlaceholderPage : ContentPage
{
    public PlaceholderPage()
    {
        InitializeComponent();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        BuildLabel.Text = $"{AppInfo.Current.VersionString} · {configuration}";
    }
}
