using Xamarin.Google.Android.Play.Core.Tasks;
using Microsoft.Maui.ApplicationModel;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.Install.Model;
using Task = System.Threading.Tasks.Task;

namespace Coo.Mobile.AppVersion;

public sealed class UpdatePrompter(TimeProvider timeProvider)
{
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private bool _hasShownPromptThisSession;

    public async Task CheckForUpdatesAsync()
    {
        // Don't check more than once per hour in the same session
        if (timeProvider.GetUtcNow() - _lastChecked < TimeSpan.FromHours(1))
            return;

        try
        {
            var appContext = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            if (appContext == null) return;

            var appUpdateManager = AppUpdateManagerFactory.Create(appContext);
            var appUpdateInfoTask = appUpdateManager.AppUpdateInfo;

            dynamic info = await GetAppUpdateInfoAsync(appUpdateInfoTask);
            if (info == null) return;

            _lastChecked = timeProvider.GetUtcNow();

            if (info.UpdateAvailability() == UpdateAvailability.UpdateAvailable)
            {
                int priority = info.UpdatePriority();
                bool isImmediateAllowed = info.IsUpdateTypeAllowed(AppUpdateType.Immediate);
                bool isFlexibleAllowed = info.IsUpdateTypeAllowed(AppUpdateType.Flexible);

                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                if (activity == null) return;

                if (priority >= 4 && isImmediateAllowed) // High priority
                {
                    appUpdateManager.StartUpdateFlowForResult(
                        info,
                        AppUpdateType.Immediate,
                        activity,
                        1001);
                }
                else if (isFlexibleAllowed && !_hasShownPromptThisSession)
                {
                    _hasShownPromptThisSession = true;
                    appUpdateManager.StartUpdateFlowForResult(
                        info,
                        AppUpdateType.Flexible,
                        activity,
                        1002);
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore failure, we will check again later
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
        }
    }

    private static Task<object> GetAppUpdateInfoAsync(Xamarin.Google.Android.Play.Core.Tasks.Task task)
    {
        var tcs = new TaskCompletionSource<object>();
        task.AddOnSuccessListener(new SuccessListener(obj => tcs.TrySetResult(obj!)));
        task.AddOnFailureListener(new FailureListener(ex => tcs.TrySetException(ex)));
        return tcs.Task;
    }

    private sealed class SuccessListener : Java.Lang.Object, IOnSuccessListener
    {
        private readonly Action<Java.Lang.Object?> _onSuccess;
        public SuccessListener(Action<Java.Lang.Object?> onSuccess) => _onSuccess = onSuccess;
        public void OnSuccess(Java.Lang.Object? result) => _onSuccess(result);
    }

    private sealed class FailureListener : Java.Lang.Object, IOnFailureListener
    {
        private readonly Action<Java.Lang.Exception> _onFailure;
        public FailureListener(Action<Java.Lang.Exception> onFailure) => _onFailure = onFailure;
        public void OnFailure(Java.Lang.Exception? e)
        {
            if (e != null) _onFailure(e);
        }
    }
}
