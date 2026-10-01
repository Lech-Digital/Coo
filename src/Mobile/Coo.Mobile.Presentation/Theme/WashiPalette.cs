namespace Coo.Mobile.Presentation.Theme;

public enum PaletteKind
{
    Day,
    Night,
}

/// <summary>
/// One mode's colour tokens (design system §2). The single source of truth in code: the host builds its
/// resource dictionary from it, and a test keeps it identical to <c>docs/design/design-system.md</c> (Spec 005 §2).
/// </summary>
public sealed record Palette(
    PaletteKind Kind,
    string Ground, string Surface, string Line, string Ink, string Ink2,
    string Accent, string AccentSoft, string AccentInk, string OnAccent,
    string Cheek, string CheekInk, string VoiceBg, string VoiceInk, string VoiceHint,
    string FeedTint, string FeedMark, string FeedInk,
    string DiaperTint, string DiaperMark, string DiaperInk,
    string SleepTint, string SleepMark, string SleepInk,
    string PumpTint, string PumpMark, string PumpInk)
{
    /// <summary>Every token by its resource key. Written out by hand: no reflection, so trimming keeps it.</summary>
    public IReadOnlyDictionary<string, string> Tokens => new Dictionary<string, string>
    {
        [nameof(Ground)] = Ground,
        [nameof(Surface)] = Surface,
        [nameof(Line)] = Line,
        [nameof(Ink)] = Ink,
        [nameof(Ink2)] = Ink2,
        [nameof(Accent)] = Accent,
        [nameof(AccentSoft)] = AccentSoft,
        [nameof(AccentInk)] = AccentInk,
        [nameof(OnAccent)] = OnAccent,
        [nameof(Cheek)] = Cheek,
        [nameof(CheekInk)] = CheekInk,
        [nameof(VoiceBg)] = VoiceBg,
        [nameof(VoiceInk)] = VoiceInk,
        [nameof(VoiceHint)] = VoiceHint,
        [nameof(FeedTint)] = FeedTint,
        [nameof(FeedMark)] = FeedMark,
        [nameof(FeedInk)] = FeedInk,
        [nameof(DiaperTint)] = DiaperTint,
        [nameof(DiaperMark)] = DiaperMark,
        [nameof(DiaperInk)] = DiaperInk,
        [nameof(SleepTint)] = SleepTint,
        [nameof(SleepMark)] = SleepMark,
        [nameof(SleepInk)] = SleepInk,
        [nameof(PumpTint)] = PumpTint,
        [nameof(PumpMark)] = PumpMark,
        [nameof(PumpInk)] = PumpInk,
    };
}

/// <summary>The two Washi UX palettes. Change a value here and in the design system together, or the build fails.</summary>
public static class WashiPalette
{
    public static Palette Day { get; } = new(
        PaletteKind.Day,
        Ground: "#F1EDE5", Surface: "#FAF8F3", Line: "#DDD6C9", Ink: "#2A2823", Ink2: "#6B665C",
        Accent: "#5E7150", AccentSoft: "#E3E7D9", AccentInk: "#46553B", OnAccent: "#FAF8F3",
        Cheek: "#E8C4B8", CheekInk: "#6E3F33", VoiceBg: "#2A2823", VoiceInk: "#F1EDE5", VoiceHint: "#B8B1A4",
        FeedTint: "#F5DCCB", FeedMark: "#CC6F3B", FeedInk: "#7A3F20",
        DiaperTint: "#F3E6B3", DiaperMark: "#A08528", DiaperInk: "#6A5410",
        SleepTint: "#E2DCEF", SleepMark: "#8E7FBE", SleepInk: "#473A78",
        PumpTint: "#D6E9E2", PumpMark: "#5A9481", PumpInk: "#2D5A4C");

    public static Palette Night { get; } = new(
        PaletteKind.Night,
        Ground: "#16130F", Surface: "#211D18", Line: "#332D26", Ink: "#E9C9A0", Ink2: "#A88E70",
        Accent: "#C9A46A", AccentSoft: "#37322B", AccentInk: "#E9C9A0", OnAccent: "#16130F",
        Cheek: "#2E2820", CheekInk: "#E9C9A0", VoiceBg: "#C9A46A", VoiceInk: "#16130F", VoiceHint: "#4A3D2A",
        FeedTint: "#2E2219", FeedMark: "#C98A63", FeedInk: "#D9A07C",
        DiaperTint: "#2C2716", DiaperMark: "#BFA34E", DiaperInk: "#CDB266",
        SleepTint: "#25222E", SleepMark: "#9A8AB5", SleepInk: "#C0A8C0",
        PumpTint: "#1F2724", PumpMark: "#7FA08F", PumpInk: "#98B7A6");

    public static Palette For(PaletteKind kind) => kind == PaletteKind.Night ? Night : Day;
}
