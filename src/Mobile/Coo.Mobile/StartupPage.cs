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
    private Window? _setupWindow;

    public StartupPage(IAppStartup startup, EnsureProvisionalHousehold household, IServiceProvider services)
    {
        _startup = startup;
        _household = household;
        _services = services;
        BackgroundColor = Color.FromArgb("#F1EDE5");
        Content = _status;

        // Built for a new window, before the activity offers its link (M4): the link is held until this page has routed.
        services.GetRequiredService<Joining.InvitationInbox>().Unready();
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
#if COO_DEMO
        await _services.GetRequiredService<Coo.Mobile.Application.Demo.SeedDemoLog>().ExecuteAsync(CancellationToken.None);

        // The demo shows everything, Premium included: it is local only and never sold (store-assets/PLAN.md).
        _services.GetRequiredService<Coo.Mobile.Application.Billing.IBillingService>().SetPremiumForDebug(true);
#endif

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

        // An invitation link that opened Coo (spec §4.1) becomes the pending invitation now that the database is open; a
        // pending invitation or an unfinished join opens the flow over the first real page.
        var inbox = _services.GetRequiredService<Joining.InvitationInbox>();
        var (held, openJoin) = await JoinAtStartAsync(inbox);

        // The first real page (Spec 006 §3): setup when the household has no baby yet, Today otherwise.
        // Keep the window: once another page replaces this one, this page's Window property is null.
        var window = Window;
        var households = _services.GetRequiredService<IHouseholdStore>();
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        if ((await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count > 0)
        {
            window.Page = _services.GetRequiredService<AppShell>();
            StartSync();
            inbox.MarkReady();
            if (openJoin)
                await OpenJoinAsync(held == Joining.HeldLink.Malformed);
            await PrewarmTabsAsync();
            return;
        }

        // Spec 011b §5.3: a brand-new phone opened by an invitation runs the join flow over the baby step; setup stays
        // underneath for Not now.
        ShowSetup(window, startSync: true);
        inbox.MarkReady();
        if (openJoin)
            await OpenJoinAsync(held == Joining.HeldLink.Malformed);
    }

    /// <summary>The held link stored, and whether the join flow opens at start. A failure opens nothing: the next start tries again.</summary>
    private async Task<(Joining.HeldLink Held, bool OpenJoin)> JoinAtStartAsync(Joining.InvitationInbox inbox)
    {
        try
        {
            var held = await inbox.StoreHeldAsync();
            var stage = await _services.GetRequiredService<JoinFamily>().StageAsync(CancellationToken.None);
            return (held, held == Joining.HeldLink.Malformed || stage != JoinStage.None);
        }
#pragma warning disable CA1031 // Start-up must reach a real page; the invitation, if stored, stays pending.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Invitation check at start failed: {e.GetType().Name}");
            return (Joining.HeldLink.None, false);
        }
    }

    /// <summary>
    /// The join sheet over whatever shows. When it ends over the setup step with a baby in the shown family, Today opens
    /// instead (spec §5.3); with Not now, setup simply continues. Main thread; catches everything.
    /// </summary>
    private async Task OpenJoinAsync(bool malformed)
    {
        try
        {
            // Over setup, ShowSetup has subscribed to its end already; over Today nothing follows it.
            await _services.GetRequiredService<Sync.SyncPrompter>().ShowJoinAsync(malformed);
        }
#pragma warning disable CA1031 // The invitation stays pending; the flow opens again at the next start.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Join flow did not open: {e}");
        }
    }

    // A sheet over setup closed while Today was waiting to open (M3).
    private void OnSetupModalPopped(object? sender, ModalPoppedEventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await ShowShellIfBabyAsync());

    // Raised on the main thread by the prompter; dispatched anyway, so the sheet has left the stack first.
    private void OnJoinEnded(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await ShowShellIfBabyAsync());

    /// <summary>
    /// The join ended over the setup step: with a baby in the shown family now, Today opens (spec §5.3). Only with no sheet
    /// open (M3: replacing the root page would drop it); otherwise once the last sheet closes. Catches everything.
    /// </summary>
    private async Task ShowShellIfBabyAsync()
    {
        try
        {
            // Whatever follows, this check answers the popped sheet; a sheet still open subscribes again below.
            if (_setupWindow is { } popped)
                popped.ModalPopped -= OnSetupModalPopped;
            if (_setupWindow is not { Page: Pages.SetupPage } window)
                return;
            var households = _services.GetRequiredService<IHouseholdStore>();
            if ((await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted) is not { } household
                || (await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count == 0
                || window.Page is not Pages.SetupPage setup)
                return;
            if (setup.Navigation.ModalStack.Count > 0)
            {
                window.ModalPopped += OnSetupModalPopped;
                return;
            }

            window.Page = _services.GetRequiredService<AppShell>();
            DetachSetup(window);
            StartSync();
        }
#pragma warning disable CA1031 // A main-thread callback must not end the app; setup stays.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Today did not open after joining: {e}");
        }
    }

    /// <summary>The first-launch baby step (Spec 006 §3); Today once the baby is saved.</summary>
    private void ShowSetup(Window window, bool startSync)
    {
        var setup = _services.GetRequiredService<Pages.SetupPage>();
        var model = (Coo.Mobile.Presentation.Setup.SetupViewModel)setup.BindingContext;
        _setupWindow = window;

        // Spec 011b §4.4, §5.3: "Have an invitation? Enter a code" opens the code sheet, then the join; a join that ends with a
        // baby in the family replaces setup with Today. Subscribed once, whichever opens the join.
        var prompter = _services.GetRequiredService<Sync.SyncPrompter>();
        prompter.JoinEnded -= OnJoinEnded;
        prompter.JoinEnded += OnJoinEnded;

        // The prompter outlives the window: its handlers go with the window, so none runs for a dead one.
        window.Destroying -= OnWindowDestroying;
        window.Destroying += OnWindowDestroying;
        model.EnterCodeRequested += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await prompter.ShowEnterCodeAsync();
            }
#pragma warning disable CA1031 // A main-thread callback: the setup step stays as it is.
            catch (Exception e)
#pragma warning restore CA1031
            {
                global::Android.Util.Log.Warn("Coo", $"Code sheet did not open: {e.GetType().Name}");
            }
        });
        model.Completed +=
            (_, _) => MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    window.Page = _services.GetRequiredService<AppShell>();
                    DetachSetup(window);
                }
#pragma warning disable CA1031 // A main-thread callback must not end the app; the next launch shows Today.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    global::Android.Util.Log.Warn("Coo", $"Today did not open after setup: {e}");
                    return;
                }

                // After the shell is up, in the same main-thread turn: a sheet the first cycle opens goes over Today.
                if (startSync)
                    StartSync();
            });
        window.Page = setup;
    }

    private void OnWindowDestroying(object? sender, EventArgs e)
    {
#pragma warning disable CA1031 // A window callback: nothing may escape.
        try
        {
            if (sender is Window window)
                DetachSetup(window);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    /// <summary>Setup is gone (Today replaced it, or the window is going): the join's end and the window's events no longer reach this page.</summary>
    private void DetachSetup(Window window)
    {
        _services.GetRequiredService<Sync.SyncPrompter>().JoinEnded -= OnJoinEnded;
        window.ModalPopped -= OnSetupModalPopped;
        window.Destroying -= OnWindowDestroying;
        if (ReferenceEquals(_setupWindow, window))
            _setupWindow = null;
    }

    /// <summary>
    /// Final review I1: sync made a household with no baby yet active (a refused move into a new family, or a family with
    /// no baby): Today, Log and Settings have nothing to show, so the first-launch baby step opens, as on a cold start.
    /// Main thread; catches everything.
    /// </summary>
    private async Task ShowSetupIfNoBabyAsync()
    {
        try
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows || windows[0] is not { Page: AppShell } window)
                return;
            var households = _services.GetRequiredService<IHouseholdStore>();
            if ((await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted) is not { } household
                || (await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count > 0)
                return;

            // The account's household before its first full pull (just signed in, or after a Fresh bootstrap's wipe): the
            // baby is on its way, so setup would only create a second one (found on the emulator, Task 23). The pull's own
            // DataChanged checks again.
            if (await _services.GetRequiredService<Coo.Mobile.Application.Auth.ISessionStore>().GetAsync(CancellationToken.None) is { } session
                && session.AccountHousehold == household.Id
                && !await _services.GetRequiredService<Coo.Mobile.Application.Sync.ISyncStore>().HasPulledAsync(household.Id, CancellationToken.None))
                return;

            // Checked again after the reads: another change may have opened setup meanwhile. Not while Coo is off screen or a
            // sheet is open (replacing the root page would drop it): the next change or the next launch checks again.
            if (window.Page is AppShell && App.IsForeground && window.Page.Navigation.ModalStack.Count == 0)
                ShowSetup(window, startSync: false);
        }
#pragma warning disable CA1031 // Fire-and-forget on the main thread: the next change or launch checks again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Setup check failed: {e}");
        }
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
        _ = _services.GetRequiredService<AppVersion.UpdatePrompter>().CheckForUpdatesAsync();
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

                _ = ShowSetupIfNoBabyAsync();
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

            // Reminders and the widget follow the log (ADR 0007 §3): every local write, every sync that changed data.
            Coo.Mobile.Platform.Notifications.ChannelName.Value = Coo.Mobile.Presentation.Localization.Strings.Get("Reminders.Title");
            var reminders = _services.GetRequiredService<Coo.Mobile.Presentation.Notifications.ReminderRefresher>();
            _services.GetRequiredService<Coo.Mobile.Application.Sync.LocalWrites>().Written += (_, _) => reminders.RequestSoon();
            engine.DataChanged += (_, _) => reminders.RequestSoon();
            reminders.RequestSoon();
            BackgroundSyncJob.Schedule(global::Android.App.Application.Context);

            // Started while Coo is not on screen (a Stopped that came before this page finished): no 30-second pull then.
            if (!App.IsForeground)
                engine.Backgrounded();
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
            _ = _services.GetRequiredService<Pages.JoinPage>();
            _ = _services.GetRequiredService<Pages.EnterCodePage>();
            _ = _services.GetRequiredService<Pages.RemovalPage>();
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
        // Not on screen: Resumed does this when the window comes back.
        if (!App.IsForeground)
            return;
        try
        {
            _services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>().Resumed();
            _services.GetRequiredService<Sync.SyncPrompter>().Resumed();
            _ = _services.GetRequiredService<AppVersion.UpdatePrompter>().CheckForUpdatesAsync();
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

        // Check billing entitlement in background if household is available
        _ = Task.Run(async () =>
        {
            try
            {
                var households = _services.GetService<IHouseholdStore>();
                var billing = _services.GetService<Coo.Mobile.Application.Billing.IBillingService>();
                if (households != null && billing != null)
                {
                    var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
                    if (household != null)
                    {
                        await billing.CheckEntitlementAsync(household.Id, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to sync billing entitlement at start: {ex}");
            }
        });
    }
}
