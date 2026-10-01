namespace Coo.Mobile.Application.Preferences;

/// <summary>How the app picks its palette (design system §2, "Appearance modes").</summary>
public enum AppearanceMode
{
    /// <summary>Night during quiet hours or when Android is in dark mode; day otherwise. The default.</summary>
    Auto,
    Day,
    Night,
}

/// <summary>
/// The quiet hours, in local time. They may span midnight; the start is inclusive, the end exclusive
/// (Spec 005 §6.1). Equal start and end means no quiet hours.
/// </summary>
public sealed record QuietHours(TimeOnly Start, TimeOnly End)
{
    /// <summary>22:00–07:00 (ADR 0007 §4).</summary>
    public static QuietHours Default { get; } = new(new TimeOnly(22, 0), new TimeOnly(7, 0));

    public bool Contains(TimeOnly time) =>
        Start == End ? false
        : Start < End ? time >= Start && time < End
        : time >= Start || time < End;
}

/// <summary>This phone's appearance settings. Per device, never synced: parents differ (Spec 005 §6.3).</summary>
public sealed record AppearancePreferences(AppearanceMode Mode, QuietHours QuietHours)
{
    public static AppearancePreferences Default { get; } = new(AppearanceMode.Auto, QuietHours.Default);
}

/// <summary>Settings that belong to this phone, not to the household.</summary>
public interface IDevicePreferences
{
    Task<AppearancePreferences> GetAppearanceAsync(CancellationToken cancellationToken);

    Task SetAppearanceAsync(AppearancePreferences preferences, CancellationToken cancellationToken);
}

/// <summary>Whether the phone shows 24-hour times. It follows the Android setting, not the language (ADR 0006 §5).</summary>
public interface IClockFormat
{
    bool Is24Hour { get; }
}
