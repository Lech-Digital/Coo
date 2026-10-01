using Android.App;
using Android.Content.PM;
using Android.Content.Res;

namespace Coo.Mobile;

/// <summary>
/// Locale and layout-direction changes are handled here rather than by recreating the activity: a recreation during a
/// language change crashed MAUI's Shell teardown (ObjectDisposedException in ShellFragmentContainer.OnDestroy), found on
/// a Pixel 10 when switching Coo's app language. A language change instead rebuilds the page tree, so every string
/// switches at once (ADR 0006).
/// </summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
        | ConfigChanges.Locale | ConfigChanges.LayoutDirection)]
public class MainActivity : MauiAppCompatActivity
{
    private string? _language;

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        var language = newConfig.Locales.Get(0)?.ToLanguageTag();
        if (_language is not null && language != _language)
            (Microsoft.Maui.Controls.Application.Current as App)?.ReloadForLanguage(language);
        _language = language;
    }

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _language = Resources?.Configuration?.Locales.Get(0)?.ToLanguageTag();
        OnBackPressedDispatcher.AddCallback(this, new BackToPage(this));
    }

    /// <summary>
    /// Back goes to the MAUI page tree: a sheet closes, a pushed page pops. With nothing left to close, Coo moves to
    /// the background rather than finishing the activity. Found on the Pixel 10 (Android 16) in Increment 7: Back
    /// finished the activity without asking the pages, and MAUI's Shell teardown then crashed (ObjectDisposedException
    /// in ShellFragmentContainer.OnDestroy).
    /// </summary>
    private sealed class BackToPage(Activity activity) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
            var page = windows is { Count: > 0 } ? windows[0].Page : null;
            if (page?.SendBackButtonPressed() != true)
                activity.MoveTaskToBack(true);
        }
    }
}
