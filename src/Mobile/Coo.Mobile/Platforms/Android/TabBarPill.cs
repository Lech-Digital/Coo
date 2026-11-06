using Android.Content.Res;
using Google.Android.Material.BottomNavigation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;

namespace Coo.Mobile.Platforms.Android;

/// <summary>
/// The tab bar's chosen tab, its icon and its label, sits on a soft matcha pill (design canvas, 2026-11-05; with the
/// label from 2026-11-06), in <c>AccentSoft</c> by day and night. Shell sets the bar's colours; this only adds the pill after it.
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
            // The pill holds the icon and its label together (owner, 2026-11-06), so it is the item's background when
            // checked, inset from the item's edges; Material's own indicator only fits around the icon.
            var density = bottomView.Resources?.DisplayMetrics?.Density ?? 1f;
            var shape = new global::Android.Graphics.Drawables.GradientDrawable();
            shape.SetColor(pill.ToPlatform());
            shape.SetCornerRadius(18 * density);
            // A layer inset, not an InsetDrawable: that one reports its inset as padding and squeezes the label onto the icon.
            var inset = new global::Android.Graphics.Drawables.LayerDrawable([shape]);
            inset.SetLayerInset(0, (int)(6 * density), (int)(5 * density), (int)(6 * density), (int)(5 * density));
            var states = new global::Android.Graphics.Drawables.StateListDrawable();
            states.AddState([global::Android.Resource.Attribute.StateChecked], inset);
            states.AddState([], new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.Transparent));
            bottomView.ItemActiveIndicatorEnabled = false;
            bottomView.ItemBackground = states;
        }
    }
}
