namespace Coo.Mobile;

/// <summary>
/// The "Tell Coo" app shortcut (long-press the icon, or pin it to the home screen): it opens Coo on Today, listening.
/// The request waits here until Today can take it, whether Coo was running or just starting.
/// </summary>
public static class VoiceShortcut
{
    public const string Action = "com.lechdigital.coo.action.TELL";
    private static int _pending;

    /// <summary>Raised on the thread the intent arrived on; listeners dispatch to the UI.</summary>
    public static event EventHandler? Requested;

    public static void Request()
    {
        Interlocked.Exchange(ref _pending, 1);
        Requested?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>True once per request: the first taker opens the sheet.</summary>
    public static bool TryTake() => Interlocked.Exchange(ref _pending, 0) == 1;
}
