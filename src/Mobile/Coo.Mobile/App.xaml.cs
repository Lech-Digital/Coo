using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Resources.Styles;

namespace Coo.Mobile;

// Fully qualified: inside the Coo.Mobile namespace, "Application" names the Coo.Mobile.Application project.
public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    /// <summary>
    /// The app's language changed while it ran: apply the culture, and rebuild the pages so every string is re-read
    /// (ADR 0006). The data and the palette are untouched.
    /// </summary>
    public void ReloadForLanguage(string? languageTag)
    {
        if (!string.IsNullOrEmpty(languageTag))
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(languageTag);
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        }

        if (Windows.Count > 0 && Windows[0].Page is AppShell)
            Windows[0].Page = _services.GetRequiredService<AppShell>();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<StartupPage>());
        window.Created += (_, _) => _services.GetRequiredService<ThemeHost>().Apply(PaletteKind.Day);
        window.Resumed += (_, _) => _services.GetRequiredService<ThemeController>().Refresh();
        return window;
    }
}
