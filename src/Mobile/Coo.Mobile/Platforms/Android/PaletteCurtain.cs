using Android.Views;
using Android.Widget;
using Coo.Mobile.Presentation.Theme;
using AView = Android.Views.View;

namespace Coo.Mobile.Platforms.Android;

/// <summary>
/// The day/night switch's cover (owner, 2026-11-05: "we need a nice loading screen"): a native view over the whole
/// window in the new palette's wash, with Mochi awake for day and asleep for night, breathing once. Native, so it is on
/// screen before MAUI starts repainting every page, and nothing under it shows half-switched.
/// </summary>
internal sealed class PaletteCurtain : IPaletteCurtain, IDisposable
{
    private const int FadeIn = 140;
    private const int FadeOut = 260;

    // The settle: the colour swap and each page's queued redraw run on the main thread after the change is announced.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(180);

    private FrameLayout? _cover;

    public Task CoverAsync(PaletteKind palette) => OnMain(done =>
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window?.DecorView is not ViewGroup root)
        {
            done();
            return;
        }

        Remove();
        var night = palette == PaletteKind.Night;
        var density = activity.Resources?.DisplayMetrics?.Density ?? 1f;
        var size = Math.Max(root.Width, root.Height);
        _cover = new FrameLayout(activity) { Alpha = 0, Clickable = true, Focusable = true };
        _cover.Background = WashBitmap.Drawable(night, size);

        var mochi = new ImageView(activity);
        var id = activity.Resources!.GetIdentifier(night ? "mochi_asleep" : "mochi_awake", "drawable", activity.PackageName);
        if (id != 0)
            mochi.SetImageResource(id);
        var side = (int)(96 * density);
        _cover.AddView(mochi, new FrameLayout.LayoutParams(side, side, GravityFlags.Center));

        root.AddView(_cover, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        mochi.ScaleX = mochi.ScaleY = 0.92f;
        mochi.Animate()?.ScaleX(1f).ScaleY(1f).SetDuration(FadeIn + Settle.Milliseconds + 120).Start();
        _cover.Animate()?.Alpha(1f).SetDuration(FadeIn).WithEndAction(new Java.Lang.Runnable(done))?.Start();
    });

    public async Task RevealAsync()
    {
        await Task.Delay(Settle);
        await OnMain(done =>
        {
            if (_cover is not { } cover)
            {
                done();
                return;
            }

            cover.Animate()?.Alpha(0f).SetDuration(FadeOut).WithEndAction(new Java.Lang.Runnable(() =>
            {
                Remove();
                done();
            }))?.Start();
        });
    }

    public void Dispose() => Remove();

    private void Remove()
    {
        if (_cover?.Parent is ViewGroup parent)
            parent.RemoveView(_cover);
        _cover?.Dispose();
        _cover = null;
    }

    private static Task OnMain(Action<Action> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                work(done.SetResult);
            }
            catch (Exception)
            {
                // A cover that cannot show never blocks the switch.
                done.TrySetResult();
            }
        });
        return done.Task;
    }
}
