using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Resources.Styles;

namespace Coo.Mobile;

/// <summary>
/// Runs migrations, data migrations and the integrity scan before anything else touches the database
/// (Spec 004 §7), then shows the first real page.
/// </summary>
public sealed class StartupPage : ContentPage
{
    private readonly IAppStartup _startup;
    private readonly EnsureProvisionalHousehold _household;
    private readonly IServiceProvider _services;
    private readonly Label _status = new() { Text = "Starting…", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private bool _started;

    public StartupPage(IAppStartup startup, EnsureProvisionalHousehold household, IServiceProvider services)
    {
        _startup = startup;
        _household = household;
        _services = services;
        BackgroundColor = Color.FromArgb("#F1EDE5");
        Content = _status;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
            return;
        _started = true;

        if (await _startup.RunAsync(CancellationToken.None) == StartupResult.DatabaseFromNewerApp)
        {
            // Nothing was touched. Localized wording arrives with the real screens (ADR 0006).
            _status.Text = "This data was saved by a newer version of Coo. Please update the app.";
            return;
        }

        await _household.ExecuteAsync(CancellationToken.None);

        // Who is signed in (Spec 010 §3.3), read once from SecureStorage. Signed in or not, Today opens the same way.
        try
        {
            await _services.GetRequiredService<Coo.Mobile.Application.Auth.Account>().LoadAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // An unreadable store falls back to signed out; it never blocks start-up or logging.
        }

        // The language (Spec 009 §3.5), before the first real page is built. A later change relabels the tabs, and the
        // pages redraw themselves.
        var language = _services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>();
        await language.StartAsync(App.SystemCulture, CancellationToken.None);
        var app = (App)Microsoft.Maui.Controls.Application.Current!;
        app.ApplyLanguage();
        language.Changed += (_, _) => app.ApplyLanguage();

        // The palette (Spec 005 §6.2): the controller decides, the host applies, on the main thread.
        var theme = _services.GetRequiredService<ThemeController>();
        var host = _services.GetRequiredService<ThemeHost>();
        theme.PaletteChanged += (_, kind) => MainThread.BeginInvokeOnMainThread(() => host.Apply(kind));
        await theme.StartAsync(CancellationToken.None);

        // The first real page (Spec 006 §3): setup when the household has no baby yet, Today otherwise.
        var households = _services.GetRequiredService<IHouseholdStore>();
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        if ((await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count > 0)
        {
            Window.Page = _services.GetRequiredService<AppShell>();
            StartSync();
            await PrewarmTabsAsync();
            return;
        }

        // Keep the window: once setup replaces this page, this page's Window property is null.
        var window = Window;
        var setup = _services.GetRequiredService<Pages.SetupPage>();
        ((Coo.Mobile.Presentation.Setup.SetupViewModel)setup.BindingContext).Completed +=
            (_, _) => MainThread.BeginInvokeOnMainThread(() =>
            {
                window.Page = _services.GetRequiredService<AppShell>();

                // After the shell is up, in the same main-thread turn: a sheet the first cycle opens goes over Today.
                StartSync();
            });
        window.Page = setup;
    }

    /// <summary>Set once sync has started; the window's lifecycle triggers wait for it. Once per process, like the singletons it wires.</summary>
    internal static bool SyncStarted { get; private set; }

    /// <summary>Sync starts once a real page shows (Spec 011 §6.3): the engine, connectivity, the prompts, page refreshes.</summary>
    private void StartSync()
    {
        if (SyncStarted)
        {
            // A recreated activity (the process lived on): wired already, but its old window's Stopped put the engine in
            // the background and the new window gets no Resumed. This page is the foreground again.
            ResumeSync();
            return;
        }

        SyncStarted = true;
        try
        {
            var engine = _services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>();
            var undo = _services.GetRequiredService<Coo.Mobile.Presentation.Today.UndoService>();

            // Pulled records or a household move (raised on the engine's thread): the pages show what is now stored.
            engine.DataChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    undo.ExternalChange();
                }
#pragma warning disable CA1031 // A page's refresh failing must not end the app; the next change refreshes it.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            });
            _services.GetRequiredService<Sync.SyncPrompter>().Attach();
            var connectivity = _services.GetRequiredService<Coo.Mobile.Application.Sync.IConnectivityWatcher>();
            connectivity.Regained += (_, _) =>
            {
                // Raised on an Android binder thread; the callback catches too, but this handler stands on its own.
                try
                {
                    engine.ConnectivityRegained();
                }
#pragma warning disable CA1031 // A missed signal is covered by the 30-second pull.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            };
            connectivity.Start();
            engine.Start();
        }
#pragma warning disable CA1031 // Logging never depends on sync (ADR 0002): a failure here leaves the app as it was.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Sync did not start: {e}");
        }

#if DEBUG
        // Ruling F5: the sheets resolve too (they are otherwise first built when a cycle asks for them).
        try
        {
            _ = _services.GetRequiredService<Pages.ConsentPage>();
            _ = _services.GetRequiredService<Pages.BringOverPage>();
        }
#pragma warning disable CA1031 // A debug-only check: report it, never end the app.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Sync sheet did not resolve: {e}");
        }
#endif
    }

    private void ResumeSync()
    {
        try
        {
            _services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>().Resumed();
            _services.GetRequiredService<Sync.SyncPrompter>().Resumed();
        }
#pragma warning disable CA1031 // Logging never depends on sync (ADR 0002); the next resume tries again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Sync did not resume: {e}");
        }
    }

    /// <summary>
    /// Log and Settings load their data now, so their first visit draws at once instead of blank-then-full (owner's
    /// finding on the Pixel 10: tabs loaded and jumped).
    /// </summary>
    private async Task PrewarmTabsAsync()
    {
        await _services.GetRequiredService<Coo.Mobile.Presentation.Log.LogViewModel>().RefreshAsync();
        await _services.GetRequiredService<Coo.Mobile.Presentation.Settings.SettingsViewModel>().RefreshAsync();
    }
}
