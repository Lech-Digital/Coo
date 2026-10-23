using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
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
        // "Tell Coo" from the launcher while another tab is open: bring Today forward, where the request is taken.
        VoiceShortcut.Requested += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Windows.Count > 0 && Windows[0].Page is AppShell shell && shell.CurrentState.Location.OriginalString.Contains("today", StringComparison.Ordinal) is false)
                await shell.GoToAsync("//today");
        });
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

        // Sync triggers (Spec 011 §6.3). Resume: a cycle at once, the 30-second pull again, and any sheet held back while in
        // the background. Background: the 30-second pull stops and one more cycle is asked for, after any backoff (Android
        // may cut it; that is safe).
        IsForeground = true;
        window.Resumed += (_, _) =>
        {
            IsForeground = true;
            try
            {
                _services.GetService<TodayViewModel>()?.CheckDayChanged();
                _services.GetService<LogViewModel>()?.ResetToToday();
                _services.GetService<UndoService>()?.ExternalChange();
            }
            catch
            {
            }
            Lifecycle((engine, prompter) =>
            {
                engine.Resumed();
                prompter.Resumed();
            });
        };
        window.Stopped += (_, _) =>
        {
            IsForeground = false;
            Lifecycle((engine, prompter) =>
            {
                engine.Backgrounded();
                prompter.Backgrounded();
            });
        };
        return window;
    }

    /// <summary>
    /// Whether a Coo window is on screen: set when a window is created (a recreated activity gets no Resumed) and on
    /// Resumed, cleared on Stopped. The prompter opens no sheet by itself while it is false.
    /// </summary>
    internal static bool IsForeground { get; private set; } = true;

    /// <summary>Only once sync has started (<see cref="StartupPage"/>): a resume during the migrations must not start a cycle.</summary>
    private void Lifecycle(Action<Coo.Mobile.Application.Sync.SyncEngine, Sync.SyncPrompter> action)
    {
        if (!StartupPage.SyncStarted)
            return;
        try
        {
            action(_services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>(), _services.GetRequiredService<Sync.SyncPrompter>());
        }
#pragma warning disable CA1031 // A window event handler: a failure here must not end the app; the 30-second pull goes on.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
