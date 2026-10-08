using Coo.Mobile.Infrastructure.AppVersion;
using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.AppVersion;

public sealed class UpdatePrompter(IVersionApi api, SheetNavigator navigator)
{
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private bool _hasShownPromptThisSession = false;

    public async Task CheckForUpdatesAsync()
    {
        // Don't check more than once per hour in the same session
        if (DateTimeOffset.UtcNow - _lastChecked < TimeSpan.FromHours(1))
            return;

        try
        {
            var info = await api.GetVersionInfoAsync(CancellationToken.None);
            if (info == null) return;

            _lastChecked = DateTimeOffset.UtcNow;

            if (int.TryParse(AppInfo.Current.BuildString, out int currentBuild))
            {
                if (currentBuild < info.Minimum)
                {
                    // Forced update
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        return navigator.OpenAsync(new UpdateSheet(true));
                    });
                }
                else if (currentBuild < info.Latest && !_hasShownPromptThisSession && info.IsForced)
                {
                    // "IsForced" from backend maybe means recommend strongly.
                    // Let's just show recommended update once per session.
                    _hasShownPromptThisSession = true;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        return navigator.OpenAsync(new UpdateSheet(false));
                    });
                }
                else if (currentBuild < info.Latest && !_hasShownPromptThisSession)
                {
                    _hasShownPromptThisSession = true;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        return navigator.OpenAsync(new UpdateSheet(false));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore failure, we will check again later
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
        }
    }
}
