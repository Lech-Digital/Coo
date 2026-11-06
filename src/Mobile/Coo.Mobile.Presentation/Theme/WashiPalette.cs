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
    string Accent, string AccentSoft, string AccentInk, string OnAccent, string Brand,
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
        [nameof(Brand)] = Brand,
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
        Ground: "#EEEEE6", Surface: "#FAFAF5", Line: "#D8DACB", Ink: "#262A22", Ink2: "#62685A",
        Accent: "#4A7036", AccentSoft: "#DFEBC8", AccentInk: "#3D5E2B", OnAccent: "#FAFAF5", Brand: "#93B86A",
        Cheek: "#DDE9C9", CheekInk: "#3D5E2B", VoiceBg: "#262A22", VoiceInk: "#EEEEE6", VoiceHint: "#B4B8AA",
        FeedTint: "#F5DCCB", FeedMark: "#CC6F3B", FeedInk: "#7A3F20",
        DiaperTint: "#F3E6B3", DiaperMark: "#A08528", DiaperInk: "#6A5410",
        SleepTint: "#E2DCEF", SleepMark: "#8E7FBE", SleepInk: "#473A78",
        PumpTint: "#DCE6EE", PumpMark: "#5B84A0", PumpInk: "#2B4A5F");

    public static Palette Night { get; } = new(
        PaletteKind.Night,
        Ground: "#121511", Surface: "#1B1F19", Line: "#2C3228", Ink: "#D9E3C8", Ink2: "#97A486",
        Accent: "#A3C47F", AccentSoft: "#283022", AccentInk: "#CFE0B6", OnAccent: "#121511", Brand: "#A3C47F",
        Cheek: "#2A2A22", CheekInk: "#D9E3C8", VoiceBg: "#A3C47F", VoiceInk: "#121511", VoiceHint: "#36412C",
        FeedTint: "#2E2219", FeedMark: "#C98A63", FeedInk: "#D9A07C",
        DiaperTint: "#2C2716", DiaperMark: "#BFA34E", DiaperInk: "#CDB266",
        SleepTint: "#25222E", SleepMark: "#9A8AB5", SleepInk: "#C0A8C0",
        PumpTint: "#1D2326", PumpMark: "#7C939B", PumpInk: "#A3B5B5");

    public static Palette For(PaletteKind kind) => kind == PaletteKind.Night ? Night : Day;
}
