using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Resources.Styles;

namespace Coo.Mobile;

// Fully qualified: inside the Coo.Mobile namespace, "Application" names the Coo.Mobile.Application project.
public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        // Read before anything changes the process culture.
        _ = SystemCulture;
        InitializeComponent();
        _services = services;
    }

    /// <summary>Android's language at process start, before Coo set a culture of its own (Spec 009 §3.5).</summary>
    public static System.Globalization.CultureInfo SystemCulture { get; } = System.Globalization.CultureInfo.CurrentUICulture;

    /// <summary>
    /// The language in effect changed (Spec 009 §3.5, ADR 0006): apply it to the process, and rebuild the pages so
    /// every string is re-read. The data and the palette are untouched. From Settings, the new shell opens on Settings.
    /// </summary>
    public void ApplyLanguage(bool returnToSettings)
    {
        var culture = Coo.Mobile.Presentation.Localization.Strings.Culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;

        if (Windows.Count == 0 || Windows[0].Page is not AppShell)
            return;
        var shell = _services.GetRequiredService<AppShell>();
        Windows[0].Page = shell;
        if (returnToSettings)
            shell.Dispatcher.Dispatch(async () => await shell.GoToAsync("//settings"));
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<StartupPage>());
        // Apply the controller's current palette, never a fixed one: on a cold start in dark mode, Created fires after
        // the controller has already chosen Night, and a hard-coded Day here overwrote it (found on the phone).
        var theme = _services.GetRequiredService<ThemeController>();
        window.Created += (_, _) => _services.GetRequiredService<ThemeHost>().Apply(theme.Current);
        window.Resumed += (_, _) => _services.GetRequiredService<ThemeController>().Refresh();
        return window;
    }
}
