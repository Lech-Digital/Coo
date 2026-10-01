using AndroidX.Core.View;
using Microsoft.Maui.Platform;

namespace Coo.Mobile.Resources.Styles;

/// <summary>The Android status and navigation bars follow the palette (design system §2).</summary>
internal static class SystemBars
{
    public static void Apply(Color background, bool lightContent)
    {
        // Fully qualified: inside Coo.Mobile, "Platform" names the Coo.Mobile.Platform project.
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window is not { } window)
            return;

        var native = background.ToPlatform();
        window.DecorView.SetBackgroundColor(native);
#pragma warning disable CA1422 // Ignored from Android 15, where the app draws edge to edge; still needed below it.
        window.SetStatusBarColor(native);
        window.SetNavigationBarColor(native);
#pragma warning restore CA1422

        if (WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller)
            return;
        controller.AppearanceLightStatusBars = !lightContent;
        controller.AppearanceLightNavigationBars = !lightContent;
    }
}
