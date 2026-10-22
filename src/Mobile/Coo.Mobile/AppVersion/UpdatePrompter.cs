using Android.Gms.Extensions;
using Coo.Mobile.Application.AppVersion;
using Coo.Mobile.Presentation.Localization;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install.Model;

namespace Coo.Mobile.AppVersion;

/// <summary>
/// Play's in-app updates (Increment 18): a normal release downloads in the background and asks for a restart once it
/// is in; a release with Play priority 4–5, or below the server's minimum version, is the full-screen update. Checked on
/// start and on every return to the app, at most once an hour.
/// </summary>
public sealed class UpdatePrompter(TimeProvider time, IAppVersionApi versions) : IDisposable
{
    private const int RequestCode = 1001;
    private IAppUpdateManager? _manager;
    private InstallListener? _listener;
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private bool _flexibleOffered;
    private bool _restartAsked;

    public async Task CheckForUpdatesAsync()
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

            var info = (AppUpdateInfo)await _manager.GetAppUpdateInfo().AsAsync<Java.Lang.Object>();

            // Downloaded while the app was away: ask on every return until it is installed.
            if (info.InstallStatus() == InstallStatus.Downloaded)
            {
                PromptRestart();
                return;
            }

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null)
                return;

            // A full-screen update that was left half-way goes on.
            if (info.UpdateAvailability() == UpdateAvailability.DeveloperTriggeredUpdateInProgress)
            {
                _manager.StartUpdateFlowForResult(info, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), RequestCode);
                return;
            }

            if (time.GetUtcNow() - _lastChecked < TimeSpan.FromHours(1))
                return;
            _lastChecked = time.GetUtcNow();

            var server = await versions.GetAsync(CancellationToken.None);
            var action = UpdatePolicy.Decide(
                int.TryParse(AppInfo.BuildString, out var code) ? code : int.MaxValue,
                server,
                info.UpdateAvailability() == UpdateAvailability.UpdateAvailable,
                info.UpdatePriority(),
                info.IsUpdateTypeAllowed(AppUpdateType.Immediate),
                info.IsUpdateTypeAllowed(AppUpdateType.Flexible),
                _flexibleOffered);

            switch (action)
            {
                case UpdateAction.Immediate:
                    _manager.StartUpdateFlowForResult(info, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), RequestCode);
                    break;
                case UpdateAction.Flexible:
                    _flexibleOffered = true;
                    _manager.StartUpdateFlowForResult(info, activity, AppUpdateOptions.NewBuilder(AppUpdateType.Flexible).Build(), RequestCode);
                    break;
                case UpdateAction.OpenStore:
                    await AskToOpenStoreAsync();
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
            var restart = await page.DisplayAlertAsync(
                Strings.Get("Update.Title"), Strings.Get("Update.Downloaded"), Strings.Get("Update.Restart"), Strings.Get("Update.Later"));
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
        await page.DisplayAlertAsync(Strings.Get("Update.Title"), Strings.Get("Update.Forced"), Strings.Get("Update.Button"));
        await Launcher.Default.OpenAsync(new Uri("https://play.google.com/store/apps/details?id=com.lechdigital.coo"));
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
