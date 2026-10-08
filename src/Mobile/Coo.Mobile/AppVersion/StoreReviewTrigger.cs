using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Plugin.StoreReview;

namespace Coo.Mobile.AppVersion;

public sealed class StoreReviewTrigger(ICareLogStore logStore, ISessionStore sessionStore, IDevicePreferences devicePrefs, TimeProvider timeProvider) : IReviewPrompter
{
    public async Task CheckAndPromptAsync(CancellationToken cancellationToken)
    {
        // Don't prompt during quiet hours
        var appearance = await devicePrefs.GetAppearanceAsync(CancellationToken.None);
        if (appearance.QuietHours.Contains(TimeOnly.FromDateTime(timeProvider.GetLocalNow().DateTime)))
            return;

        // Has already rated?
        if (Microsoft.Maui.Storage.Preferences.Default.Get("has_rated_app", false))
            return;

        // How many times have we prompted?
        var promptCount = Microsoft.Maui.Storage.Preferences.Default.Get("rating_prompt_count", 0);
        if (promptCount > 2)
            return;

        var session = await sessionStore.GetAsync(CancellationToken.None);
        if (session == null) return;

        // How many entries in total?
        var counts = await logStore.CountsAsync(session.AccountHousehold, CancellationToken.None);
        var totalEntries = counts.Entries;
        
        // Trigger at 20 entries, and then again at 100
        if ((promptCount == 0 && totalEntries >= 20) || (promptCount == 1 && totalEntries >= 100))
        {
            Microsoft.Maui.Storage.Preferences.Default.Set("rating_prompt_count", promptCount + 1);
            
            try
            {
                CrossStoreReview.Current.RequestReview(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to request review: {ex}");
            }
        }
    }

    public void MarkAsRated()
    {
        Microsoft.Maui.Storage.Preferences.Default.Set("has_rated_app", true);
    }
}
