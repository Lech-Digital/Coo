using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

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

#if DEBUG_SCREEN
        Page next = _services.GetRequiredService<DebugScreen.DebugPage>();
#else
        Page next = _services.GetRequiredService<PlaceholderPage>();
#endif
        Window.Page = next;
    }
}
