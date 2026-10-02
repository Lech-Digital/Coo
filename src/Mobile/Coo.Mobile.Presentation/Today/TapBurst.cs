namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Three quick taps, the stand-in for a long press that opens the debug screen (Spec 006 Q5). On Android, MAUI tells a
/// TapGestureRecognizer only about single and double taps, so <c>NumberOfTapsRequired = 3</c> never fired (found in
/// Spec 010a on the Pixel 10). Single taps are counted instead. Two seconds leaves room for <c>adb shell input tap</c>.
/// </summary>
public sealed class TapBurst(TimeProvider time)
{
    public const int Taps = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>True on the tap that completes three within the window; counting then starts again.</summary>
    public bool Tap()
    {
        var now = time.GetUtcNow();
        _recent.Enqueue(now);
        while (now - _recent.Peek() > Window)
            _recent.Dequeue();
        if (_recent.Count < Taps)
            return false;
        _recent.Clear();
        return true;
    }
}
