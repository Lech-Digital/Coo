using Android.Gms.Extensions;
using Coo.Mobile.Application.AppVersion;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Google.Android.Play.Core.Review;

namespace Coo.Mobile.AppVersion;

/// <summary>Play's in-app review, at the moments <see cref="ReviewPolicy"/> allows (Increment 18).</summary>
public sealed class StoreReviewTrigger(ICareLogStore logStore, IHouseholdStore households, IDevicePreferences devicePrefs, TimeProvider timeProvider) : IReviewPrompter
{
    private const string FirstUseKey = "review.first_use";
    private const string LastAskedKey = "review.last_asked";
    private const string AskedKey = "review.asked";
    private bool _asking;

    public async Task CheckAndPromptAsync(CancellationToken cancellationToken)
    {
        if (_asking)
            return;
        _asking = true;
        try
        {
            var prefs = Microsoft.Maui.Storage.Preferences.Default;
            var now = timeProvider.GetUtcNow();
            if (!prefs.ContainsKey(FirstUseKey))
                prefs.Set(FirstUseKey, now.ToUnixTimeSeconds());
            var firstUse = DateTimeOffset.FromUnixTimeSeconds(prefs.Get(FirstUseKey, now.ToUnixTimeSeconds()));
            DateTimeOffset? lastAsked = prefs.ContainsKey(LastAskedKey) ? DateTimeOffset.FromUnixTimeSeconds(prefs.Get(LastAskedKey, 0L)) : null;
            var asked = prefs.Get(AskedKey, 0);

            var appearance = await devicePrefs.GetAppearanceAsync(cancellationToken);
            var quiet = appearance.QuietHours.Contains(TimeOnly.FromDateTime(timeProvider.GetLocalNow().DateTime));
            var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
            if (household is null)
                return;
            var entries = await logStore.CountAllEntriesAsync(household.Id, cancellationToken);
            if (!ReviewPolicy.ShouldAsk(firstUse, now, entries, lastAsked, asked, quiet))
                return;

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null)
                return;
            // Counted before Play is asked: Play may decide not to show its dialog, and it never says.
            prefs.Set(LastAskedKey, now.ToUnixTimeSeconds());
            prefs.Set(AskedKey, asked + 1);

            // A moment after the save's own feedback (the toast), not on top of it.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var manager = ReviewManagerFactory.Create(activity);
            var info = (ReviewInfo)await manager.RequestReviewFlow().AsAsync<Java.Lang.Object>();
            await manager.LaunchReviewFlow(activity, info).AsAsync<Java.Lang.Object>();
        }
#pragma warning disable CA1031 // A review prompt never breaks logging.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine($"Review prompt failed: {ex}");
        }
        finally
        {
            _asking = false;
        }
    }
}
