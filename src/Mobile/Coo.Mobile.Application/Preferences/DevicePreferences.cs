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

/// <summary>"Day for now" from Today (Spec 009 §3.7): in Auto, <see cref="Palette"/> (Day or Night) until <see cref="Until"/>.</summary>
public sealed record AppearanceOverride(AppearanceMode Palette, DateTimeOffset Until);

/// <summary>This phone's appearance settings. Per device, never synced: parents differ (Spec 005 §6.3).</summary>
public sealed record AppearancePreferences(AppearanceMode Mode, QuietHours QuietHours, AppearanceOverride? Override = null)
{
    public static AppearancePreferences Default { get; } = new(AppearanceMode.Auto, QuietHours.Default);
}

/// <summary>
/// The app's language on this phone (ADR 0006 §1): System (Android's language if Coo has it), or a two-letter tag.
/// Per device, never synced. Which tags are supported is the presentation's business.
/// </summary>
public sealed record LanguagePreference(string? Language)
{
    public static LanguagePreference System { get; } = new((string?)null);

    public bool IsSystem => Language is null;
}

/// <summary>Settings that belong to this phone, not to the household.</summary>
public interface IDevicePreferences
{
    Task<AppearancePreferences> GetAppearanceAsync(CancellationToken cancellationToken);

    Task SetAppearanceAsync(AppearancePreferences preferences, CancellationToken cancellationToken);

    Task<LanguagePreference> GetLanguageAsync(CancellationToken cancellationToken);

    Task SetLanguageAsync(LanguagePreference preference, CancellationToken cancellationToken);

    Task<string?> GetBabyAvatarAsync(Guid babyId, CancellationToken cancellationToken);

    Task SetBabyAvatarAsync(Guid babyId, string? avatarRef, CancellationToken cancellationToken);

    /// <summary>The nanny's handover: the last of a caregiver's entries the parent marked seen on this phone.</summary>
    Task<DateTimeOffset?> GetHandoverSeenAsync(Guid person, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);

    Task SetHandoverSeenAsync(Guid person, DateTimeOffset lastSeen, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Whether the phone shows 24-hour times. It follows the Android setting, not the language (ADR 0006 §5).</summary>
public interface IClockFormat
{
    bool Is24Hour { get; }
}
