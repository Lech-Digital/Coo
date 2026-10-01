using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Theme;

/// <summary>Which palette applies, as a pure function of the inputs (Spec 005 §6.1).</summary>
public static class AppearanceRule
{
    private static readonly int[] TodayAndTomorrow = [0, 1];

    public static PaletteKind Resolve(AppearanceMode mode, TimeOnly localTime, QuietHours quietHours, bool osIsDark)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        return mode switch
        {
            AppearanceMode.Day => PaletteKind.Day,
            AppearanceMode.Night => PaletteKind.Night,
            _ => osIsDark || quietHours.Contains(localTime) ? PaletteKind.Night : PaletteKind.Day,
        };
    }

    /// <summary>As above, with the Today switch (Spec 009 §3.7): in Auto, an override that has not expired wins.</summary>
    public static PaletteKind Resolve(AppearanceMode mode, DateTimeOffset now, QuietHours quietHours, bool osIsDark, AppearanceOverride? @override) =>
        mode == AppearanceMode.Auto && @override is { } o && now < o.Until
            ? (o.Palette == AppearanceMode.Night ? PaletteKind.Night : PaletteKind.Day)
            : Resolve(mode, TimeOnly.FromDateTime(now.DateTime), quietHours, osIsDark);

    /// <summary>
    /// The next local moment at which the result can change on its own: the next quiet-hours start or end.
    /// Null when nothing time-based can change it (Day, Night, or no quiet hours).
    /// </summary>
    public static DateTimeOffset? NextChange(AppearanceMode mode, DateTimeOffset localNow, QuietHours quietHours, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        ArgumentNullException.ThrowIfNull(zone);
        if (mode != AppearanceMode.Auto || quietHours.Start == quietHours.End)
            return null;

        var today = DateOnly.FromDateTime(localNow.DateTime);
        return TodayAndTomorrow
            .SelectMany(day => new[] { quietHours.Start, quietHours.End }.Select(t => today.AddDays(day).ToDateTime(t)))
            .Select(local => new DateTimeOffset(local, zone.GetUtcOffset(local)))
            .Where(at => at > localNow)
            .Min();
    }
}
