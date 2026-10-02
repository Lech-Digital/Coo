using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using AndroidX.Core.View;

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
            IPlatformApplication.Current?.Services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>()
                .SystemChanged(System.Globalization.CultureInfo.GetCultureInfo(language!));
        _language = language;
    }

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _language = Resources?.Configuration?.Locales.Get(0)?.ToLanguageTag();
        OnBackPressedDispatcher.AddCallback(this, new BackToPage(this));

        if (Window?.DecorView.FindViewById(Android.Resource.Id.Content) is { } content)
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarsInsets());
    }

    /// <summary>
    /// The one place insets are handled, as in PurePrep and CoreChoice: the content view, which holds the pages and the
    /// tab bar, is padded clear of the status bar, the navigation bar and the cutout, and the insets are consumed so no
    /// page applies them again. Left to each page (MAUI 10's safe area), a page shown by a tab switch drew one frame
    /// without the status bar's inset and then dropped by its height: the jump on every tab change, recorded on the
    /// Pixel 10. The keyboard is padded here too, so a sheet's fields and its Save button stay above it.
    /// </summary>
    private sealed class SystemBarsInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null || WindowInsetsCompat.Consumed is not { } consumed)
                return insets!;

            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            // Max, not sum: the keyboard's inset already includes the navigation bar it covers.
            var bottom = Math.Max(bars?.Bottom ?? 0, insets.GetInsets(WindowInsetsCompat.Type.Ime())?.Bottom ?? 0);
            v.SetPadding(bars?.Left ?? 0, bars?.Top ?? 0, bars?.Right ?? 0, bottom);
            return consumed;
        }
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
