namespace Coo.Mobile.Presentation.Theme;

/// <summary>
/// The line icons (design system §5), as SVG path data on a 24-unit grid, drawn by the host's <c>Icon</c> control
/// with a 1.7 stroke, round caps and joins, and no fill (Spec 005 §7). Taken from <c>washi-ux.html</c>.
/// </summary>
public static class IconGeometry
{
    public const string Feed = "M9 3h6M10 3v3l-2 3v10a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2V9l-2-3V3M8 13h8";
    public const string Diaper = "M12 3.5c3 4 6 7.2 6 10.5a6 6 0 0 1-12 0c0-3.3 3-6.5 6-10.5z";
    public const string Sleep = "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z";
    public const string Pump = "M5 5h14l-5 5v3h-4v-3zM10 13v6a2 2 0 0 0 4 0v-6";
    public const string Mic = "M9 6a3 3 0 0 1 6 0v5a3 3 0 0 1-6 0zM5 11a7 7 0 0 0 14 0M12 18v3";
    public const string Today = "M4 11l8-7 8 7v9h-5v-6h-6v6H4z";
    public const string Log = "M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01";
    public const string Insights = "M5 20V11M11 20V5M17 20v-6M3 20h18";
    public const string Family = "M6 8a3 3 0 1 0 6 0a3 3 0 1 0-6 0M3 20a6 6 0 0 1 12 0M16 5a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 6";
    /// <summary>The Today switch to day colours (Spec 009 §3.7); the switch to night uses <see cref="Sleep"/>, the moon.</summary>
    public const string Sun = "M12 8.5a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4l1.4-1.4M17 7l1.4-1.4";
    public const string ChevronLeft = "M15 6l-6 6 6 6";
    public const string ChevronRight = "M9 6l6 6-6 6";

    /// <summary>The left breast: a half-disc facing left, closed by its diameter.</summary>
    public const string SideLeft = "M12 4a8 8 0 0 0 0 16z";

    /// <summary>The right breast: a half-disc facing right.</summary>
    public const string SideRight = "M12 4a8 8 0 0 1 0 16z";

    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>
    {
        [nameof(Feed)] = Feed,
        [nameof(Diaper)] = Diaper,
        [nameof(Sleep)] = Sleep,
        [nameof(Pump)] = Pump,
        [nameof(Mic)] = Mic,
        [nameof(Today)] = Today,
        [nameof(Log)] = Log,
        [nameof(Insights)] = Insights,
        [nameof(Family)] = Family,
        [nameof(Sun)] = Sun,
        [nameof(ChevronLeft)] = ChevronLeft,
        [nameof(ChevronRight)] = ChevronRight,
        [nameof(SideLeft)] = SideLeft,
        [nameof(SideRight)] = SideRight,
    };
}
