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
    /// The language in effect changed (Spec 009 §3.5, ADR 0006): apply it to the process (date pickers format with it)
    /// and relabel the tabs. The pages on screen redraw themselves from <c>LanguageController.Changed</c>, and the others
    /// when they next appear. The data and the palette are untouched.
    /// </summary>
    public void ApplyLanguage()
    {
        var culture = Coo.Mobile.Presentation.Localization.Strings.Culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;

        if (Windows.Count > 0 && Windows[0].Page is AppShell shell)
            shell.Relabel();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<StartupPage>());
        // Apply the controller's current palette, never a fixed one: on a cold start in dark mode, Created fires after
        // the controller has already chosen Night, and a hard-coded Day here overwrote it (found on the phone).
        var theme = _services.GetRequiredService<ThemeController>();
        window.Created += (_, _) => _services.GetRequiredService<ThemeHost>().Apply(theme.Current);
        window.Resumed += (_, _) => _services.GetRequiredService<ThemeController>().Refresh();

        // Sync triggers (Spec 011 §6.3): resume, and one last cycle on the way to the background (Android may cut it; safe).
        window.Resumed += (_, _) => Lifecycle(e => e.Resumed());
        window.Stopped += (_, _) => Lifecycle(e => e.Backgrounded());
        return window;
    }

    /// <summary>Only once sync has started (<see cref="StartupPage"/>): a resume during the migrations must not start a cycle.</summary>
    private void Lifecycle(Action<Coo.Mobile.Application.Sync.SyncEngine> action)
    {
        if (!StartupPage.SyncStarted)
            return;
        try
        {
            action(_services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>());
        }
#pragma warning disable CA1031 // A window event handler: a failure here must not end the app; the 30-second pull goes on.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
