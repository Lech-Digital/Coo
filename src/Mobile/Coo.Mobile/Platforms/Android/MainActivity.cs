using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using AndroidX.Core.View;

namespace Coo.Mobile;

/// <summary>
/// Locale and layout-direction changes are handled here rather than by recreating the activity: a recreation during a
/// language change crashed MAUI's Shell teardown (ObjectDisposedException in ShellFragmentContainer.OnDestroy), found on
/// a Pixel 10 when switching Coo's app language. A language change instead rebuilds the page tree, so every string
/// switches at once (ADR 0006). Invitation links (Spec 011b §4.1) are an App Link on the API's host; the activity stays
/// <c>SingleTop</c>, so a link while Coo runs arrives in <see cref="OnNewIntent"/>.
/// </summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
        | ConfigChanges.Locale | ConfigChanges.LayoutDirection)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "https", DataHost = "api.coo.lechdigital.nl", DataPathPrefix = "/i/", AutoVerify = true)]
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

        PublishShortcuts();

        // Only a fresh start: a recreated activity, or one reopened from Recents, carries the link it was first opened with.
        if (savedInstanceState is null && (Intent?.Flags & ActivityFlags.LaunchedFromHistory) == 0)
            Offer(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Offer(intent);
    }

    /// <summary>"Tell Coo" on a long-press of the icon (ADR 0005): one tap from the launcher to talking.</summary>
    private void PublishShortcuts()
    {
        try
        {
            var intent = new Intent(this, typeof(MainActivity)).SetAction(VoiceShortcut.Action);
            // The binding marks every builder step nullable; each returns the same builder.
            var builder = new AndroidX.Core.Content.PM.ShortcutInfoCompat.Builder(this, "tell");
            builder.SetShortLabel(Coo.Mobile.Presentation.Localization.Strings.Get("Quick.Title"));
            builder.SetIcon(AndroidX.Core.Graphics.Drawable.IconCompat.CreateWithResource(this, Resource.Mipmap.appicon));
            builder.SetIntent(intent);
            var tell = builder.Build();
            AndroidX.Core.Content.PM.ShortcutManagerCompat.PushDynamicShortcut(this, tell);
        }
#pragma warning disable CA1031 // A launcher without shortcuts must not stop Coo from starting.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Shortcut not published: {e.GetType().Name}");
        }
    }

    /// <summary>
    /// An invitation link (spec §4.1) goes to the inbox; nothing here may escape into Android (Increment 10 lesson). The
    /// link holds a code, so it is never logged.
    /// </summary>
    private static void Offer(Intent? intent)
    {
        if (intent?.Action == VoiceShortcut.Action)
        {
            VoiceShortcut.Request();
            return;
        }

        try
        {
            if (intent?.Action != Intent.ActionView || intent.Data?.ToString() is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var link))
                return;
            IPlatformApplication.Current?.Services.GetRequiredService<Joining.InvitationInbox>().Offer(link);
        }
#pragma warning disable CA1031 // An Android callback: a link that cannot be read is simply not offered.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Invitation link not taken: {e.GetType().Name}");
        }
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
