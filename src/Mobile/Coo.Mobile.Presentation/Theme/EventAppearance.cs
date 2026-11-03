namespace Coo.Mobile.Presentation.Theme;

/// <summary>
/// How a kind looks: its token prefix (<c>Feed</c> gives <c>FeedTint</c>, <c>FeedMark</c>, <c>FeedInk</c>) and its icon.
/// Every event has a colour and an icon, always together (design system §1). A kind without an entry here falls
/// back to the neutral look, so an unknown or new kind still renders.
/// </summary>
public sealed record EventAppearance(string TokenPrefix, string Icon)
{
    public static EventAppearance Neutral { get; } = new("Accent", IconGeometry.Log);

    private static readonly Dictionary<string, EventAppearance> ByKind = new(StringComparer.Ordinal)
    {
        ["feeding"] = new("Feed", IconGeometry.Feed),
        ["diaper"] = new("Diaper", IconGeometry.Diaper),
        ["sleep"] = new("Sleep", IconGeometry.Sleep),
        ["pump"] = new("Pump", IconGeometry.Pump),
        // Growth and health reuse the four kinds' palettes rather than adding colour tokens.
        ["growth"] = new("Pump", IconGeometry.Growth),
        ["medicine"] = new("Diaper", IconGeometry.Medicine),
        ["temperature"] = new("Feed", IconGeometry.Temperature),
        ["vaccine"] = new("Sleep", IconGeometry.Vaccine),
        ["visit"] = new("Pump", IconGeometry.Visit),
        ["bath"] = new("Sleep", IconGeometry.Bath),
        ["tummy"] = new("Diaper", IconGeometry.Tummy),
        ["solids"] = new("Feed", IconGeometry.Solids),
        ["milestone"] = new("Pump", IconGeometry.Milestone),
        ["sleepplan"] = new("Sleep", IconGeometry.Sleep),
    };

    public string Tint => TokenPrefix == "Accent" ? "AccentSoft" : TokenPrefix + "Tint";

    public string Mark => TokenPrefix == "Accent" ? "Accent" : TokenPrefix + "Mark";

    public string Ink => TokenPrefix == "Accent" ? "AccentInk" : TokenPrefix + "Ink";

    public static EventAppearance For(string kindCode) => ByKind.GetValueOrDefault(kindCode, Neutral);
}
