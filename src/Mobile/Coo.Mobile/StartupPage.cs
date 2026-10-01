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

        // The language (Spec 009 §3.5), before the first real page is built. A later change rebuilds the pages; from
        // Settings, the new shell opens on Settings again.
        var language = _services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>();
        await language.StartAsync(App.SystemCulture, CancellationToken.None);
        var app = (App)Microsoft.Maui.Controls.Application.Current!;
        app.ApplyLanguage(returnToSettings: false);
        language.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
            app.ApplyLanguage(returnToSettings: Shell.Current?.CurrentState.Location.OriginalString.Contains("settings", StringComparison.Ordinal) == true));

        // The palette (Spec 005 §6.2): the controller decides, the host applies, on the main thread.
        var theme = _services.GetRequiredService<ThemeController>();
        var host = _services.GetRequiredService<ThemeHost>();
        theme.PaletteChanged += (_, kind) => MainThread.BeginInvokeOnMainThread(async () => await host.ApplyAnimatedAsync(kind));
        await theme.StartAsync(CancellationToken.None);

        // The first real page (Spec 006 §3): setup when the household has no baby yet, Today otherwise.
        var households = _services.GetRequiredService<IHouseholdStore>();
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        if ((await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count > 0)
        {
            Window.Page = _services.GetRequiredService<AppShell>();
            await PrewarmTabsAsync();
            return;
        }

        // Keep the window: once setup replaces this page, this page's Window property is null.
        var window = Window;
        var setup = _services.GetRequiredService<Pages.SetupPage>();
        ((Coo.Mobile.Presentation.Setup.SetupViewModel)setup.BindingContext).Completed +=
            (_, _) => MainThread.BeginInvokeOnMainThread(() => window.Page = _services.GetRequiredService<AppShell>());
        window.Page = setup;
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
