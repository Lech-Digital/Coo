using Android.Content.Res;
using Google.Android.Material.BottomNavigation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;

namespace Coo.Mobile.Platforms.Android;

/// <summary>
/// The tab bar's chosen tab sits on a soft matcha pill (design canvas, 2026-11-05): Material's active indicator, in
/// <c>AccentSoft</c> by day and night. Shell sets the bar's colours; this only adds the pill after it.
/// </summary>
internal sealed class CooShellRenderer : ShellRenderer
{
    protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
        new PillTracker(this, shellItem);

    private sealed class PillTracker(IShellContext context, ShellItem item) : ShellBottomNavViewAppearanceTracker(context, item)
    {
        public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
        {
            base.SetAppearance(bottomView, appearance);
            Paint(bottomView);
        }

        public override void ResetAppearance(BottomNavigationView bottomView)
        {
            base.ResetAppearance(bottomView);
            Paint(bottomView);
        }

        private static void Paint(BottomNavigationView bottomView)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue("AccentSoft", out var value) != true || value is not Color pill)
                return;
            // MAUI's theme is Material Components, where the indicator has no size or shape of its own: 56 × 30 dp, round.
            var density = bottomView.Resources?.DisplayMetrics?.Density ?? 1f;
            bottomView.ItemActiveIndicatorEnabled = true;
            bottomView.ItemActiveIndicatorWidth = (int)(56 * density);
            bottomView.ItemActiveIndicatorHeight = (int)(30 * density);
            bottomView.ItemActiveIndicatorShapeAppearance = Google.Android.Material.Shape.ShapeAppearanceModel.InvokeBuilder()
                .SetAllCorners(Google.Android.Material.Shape.CornerFamily.Rounded, 15 * density)
                .Build();
            bottomView.ItemActiveIndicatorColor = ColorStateList.ValueOf(pill.ToPlatform());
        }
    }
}
