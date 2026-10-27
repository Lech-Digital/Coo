using Android.Gms.Extensions;
using Coo.Mobile.Application.AppVersion;
using Coo.Mobile.Presentation.Localization;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install.Model;

namespace Coo.Mobile.AppVersion;

/// <summary>
/// Play's in-app updates (Increment 18): a normal release downloads in the background and asks for a restart once it
/// is in; a release with Play priority 4–5, or below the server's minimum version, is the full-screen update. When Play
/// offers nothing but the server knows a newer version, Coo says so itself, once a day. Checked on start and on every
/// return to the app, at most once an hour.
/// </summary>
public sealed class UpdatePrompter(TimeProvider time, IAppVersionApi versions) : IDisposable
{
    private const int RequestCode = 1001;
    private const string StoreUrl = "https://play.google.com/store/apps/details?id=com.lechdigital.coo";
    private IAppUpdateManager? _manager;
    private InstallListener? _listener;
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private bool _flexibleOffered;
    private bool _restartAsked;

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await PlayInfoAsync();

            // Downloaded while the app was away: ask on every return until it is installed.
            if (info?.InstallStatus() == InstallStatus.Downloaded)
            {
                PromptRestart();
                return;
            }

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null)
                return;

            // A full-screen update that was left half-way goes on.
            if (info?.UpdateAvailability() == UpdateAvailability.DeveloperTriggeredUpdateInProgress)
            {
                _manager!.StartUpdateFlowForResult(info, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), RequestCode);
                return;
            }

            if (time.GetUtcNow() - _lastChecked < TimeSpan.FromHours(1))
                return;
            _lastChecked = time.GetUtcNow();

            var code = int.TryParse(AppInfo.BuildString, out var parsed) ? parsed : int.MaxValue;
            var server = await versions.GetAsync(CancellationToken.None);
            var action = UpdatePolicy.Decide(
                code,
                server,
                info?.UpdateAvailability() == UpdateAvailability.UpdateAvailable,
                info?.UpdatePriority() ?? 0,
                info?.IsUpdateTypeAllowed(AppUpdateType.Immediate) ?? false,
                info?.IsUpdateTypeAllowed(AppUpdateType.Flexible) ?? false,
                _flexibleOffered);

            // Immediate and Flexible come only from an update Play offered, so Play answered.
            switch (action)
            {
                case UpdateAction.Immediate:
                    _manager!.StartUpdateFlowForResult(info!, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), RequestCode);
                    break;
                case UpdateAction.Flexible:
                    _flexibleOffered = true;
                    _manager!.StartUpdateFlowForResult(info!, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Flexible).Build(), RequestCode);
                    break;
                case UpdateAction.OpenStore:
                    await AskToOpenStoreAsync();
                    break;
                default:
                    await SuggestAsync(code, server);
                    break;
            }
        }
#pragma warning disable CA1031 // An update check never breaks the app; the next return tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
        }
    }

    /// <summary>What Play offers this copy; null when Play cannot say (not installed from Play, no Play services, offline).</summary>
    private async Task<AppUpdateInfo?> PlayInfoAsync()
    {
        try
        {
            var context = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            _manager ??= AppUpdateManagerFactory.Create(context);
            if (_listener is null)
            {
                _listener = new InstallListener(state =>
                {
                    if (state.InstallStatus() == InstallStatus.Downloaded)
                        PromptRestart();
                });
                _manager.RegisterListener(_listener);
            }

            return (AppUpdateInfo)await _manager.GetAppUpdateInfo().AsAsync<Java.Lang.Object>();
        }
#pragma warning disable CA1031 // Play's answer is optional: the server's version still counts.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine($"Play update info failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The server knows a newer version and nothing else asked (Play's in-app update lags or is not offered): "a new
    /// version is on Google Play", at most once a day; Update opens the store.
    /// </summary>
    private async Task SuggestAsync(int code, AppVersionPolicy? server)
    {
        const string Key = "update.suggested_at";
        var preferences = Microsoft.Maui.Storage.Preferences.Default;
        var last = preferences.Get(Key, 0L) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null;
        var now = time.GetUtcNow();
        if (!UpdatePolicy.ShouldSuggest(code, server, last, now) || CurrentPage() is not { } page)
            return;
        preferences.Set(Key, now.UtcTicks);
        var update = await Components.CooDialog.ConfirmAsync(
            page, Strings.Get("Update.Title"), Strings.Get("Update.Available"), Strings.Get("Update.Button"), Strings.Get("Update.Later"));
        if (update)
            await Launcher.Default.OpenAsync(new Uri(StoreUrl));
    }

    public void Dispose()
    {
        if (_listener is not null)
            _manager?.UnregisterListener(_listener);
        _listener?.Dispose();
        _listener = null;
    }

    private static Page? CurrentPage() =>
        Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows ? windows[0].Page : null;

    private void PromptRestart()
    {
        if (_restartAsked)
            return;
        _restartAsked = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = CurrentPage();
            if (page is null)
                return;
            var restart = await Components.CooDialog.ConfirmAsync(page, Strings.Get("Update.Title"), Strings.Get("Update.Downloaded"), Strings.Get("Update.Restart"), Strings.Get("Update.Later"));
            _restartAsked = false;
            if (restart)
                _manager?.CompleteUpdate();
        });
    }

    private static async Task AskToOpenStoreAsync()
    {
        var page = CurrentPage();
        if (page is null)
            return;
        await Components.CooDialog.AlertAsync(page, Strings.Get("Update.Title"), Strings.Get("Update.Forced"), Strings.Get("Update.Button"));
        await Launcher.Default.OpenAsync(new Uri(StoreUrl));
    }

    private sealed class InstallListener(Action<InstallState> onState) : Java.Lang.Object, IInstallStateUpdatedListener
    {
        public void OnStateUpdate(InstallState? state)
        {
            if (state is not null)
                onState(state);
        }
    }
}
