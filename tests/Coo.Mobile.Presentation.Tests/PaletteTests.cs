using System.Text.RegularExpressions;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The colour rules of the design system, for both palettes (Spec 005 §3).</summary>
public sealed partial class PaletteTests
{
    /// <summary>
    /// Clearly noticeable at a glance. Event tints are never the only cue: every event also has its icon and ink
    /// (design system §1), and night tints are near-black by design (Spec 005 §3).
    /// </summary>
    private const double MinimumEventDeltaE = 5;

    public static TheoryData<string> Palettes => ["Day", "Night"];

    [Fact]
    public void Tokens_MatchDesignSystemDocument()
    {
        // Arrange
        var document = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "design-system.md"));
        var (dayDoc, nightDoc) = ParseDocument(document);

        // Act
        var day = WashiPalette.Day.Tokens;
        var night = WashiPalette.Night.Tokens;

        // Assert: same keys, same values, both modes.
        day.Should().BeEquivalentTo(dayDoc, "WashiPalette.Day must match design-system.md");
        night.Should().BeEquivalentTo(nightDoc, "WashiPalette.Night must match design-system.md");
    }

    public static TheoryData<string, string, string> TextPairs()
    {
        var data = new TheoryData<string, string, string>();
        string[][] pairs =
        [
            ["Ink", "Ground"], ["Ink", "Surface"], ["Ink2", "Ground"], ["Ink2", "Surface"], ["OnAccent", "Accent"],
            ["AccentInk", "AccentSoft"], ["VoiceInk", "VoiceBg"], ["VoiceHint", "VoiceBg"], ["CheekInk", "Cheek"],
            ["FeedInk", "FeedTint"], ["DiaperInk", "DiaperTint"], ["SleepInk", "SleepTint"], ["PumpInk", "PumpTint"],
        ];
        foreach (var palette in new[] { "Day", "Night" })
        {
            foreach (var pair in pairs)
                data.Add(palette, pair[0], pair[1]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Contrast_TextPairs_MeetWcagAA(string palette, string foreground, string background)
    {
        // Arrange
        var tokens = Pick(palette).Tokens;

        // Act
        var ratio = ColourMath.ContrastRatio(tokens[foreground], tokens[background]);

        // Assert
        ratio.Should().BeGreaterThanOrEqualTo(4.5, $"{foreground} on {background} ({palette}) is text");
    }

    public static TheoryData<string, string, string> GraphicPairs()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var palette in new[] { "Day", "Night" })
        {
            data.Add(palette, "Accent", "Ground");
            foreach (var mark in new[] { "FeedMark", "DiaperMark", "SleepMark", "PumpMark" })
            {
                data.Add(palette, mark, "Ground");
                data.Add(palette, mark, "Surface");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GraphicPairs))]
    public void Contrast_GraphicsAndLargeText_MeetThreeToOne(string palette, string foreground, string background)
    {
        // Arrange
        var tokens = Pick(palette).Tokens;

        // Act
        var ratio = ColourMath.ContrastRatio(tokens[foreground], tokens[background]);

        // Assert
        ratio.Should().BeGreaterThanOrEqualTo(3.0, $"{foreground} on {background} ({palette}) carries meaning (WCAG 1.4.11)");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Palette_ContainsNoPureWhite(string palette)
    {
        // Arrange
        var tokens = Pick(palette).Tokens;

        // Act
        var white = tokens.Where(t => t.Value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)).Select(t => t.Key);

        // Assert
        white.Should().BeEmpty("the design system forbids pure white (§9)");
    }

    [Fact]
    public void Night_TextColours_AreNotBlueDominant()
    {
        // Arrange
        var tokens = WashiPalette.Night.Tokens;
        string[] text = ["Ink", "Ink2", "AccentInk", "OnAccent", "CheekInk", "VoiceInk", "VoiceHint", "FeedInk", "DiaperInk", "SleepInk", "PumpInk"];

        // Act
        var blue = text.Where(t =>
        {
            var (r, g, b) = ColourMath.Parse(tokens[t]);
            return b > r && b > g;
        });

        // Assert
        blue.Should().BeEmpty("no blue-dominant text at night (§1)");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Events_AreDistinctFromEachOtherAndFromAccent(string palette)
    {
        // Arrange
        var tokens = Pick(palette).Tokens;
        string[] tints = ["FeedTint", "DiaperTint", "SleepTint", "PumpTint", "AccentSoft"];

        // Act
        var tooClose = (from a in tints
                        from b in tints
                        where string.CompareOrdinal(a, b) < 0
                        let delta = ColourMath.DeltaE2000(tokens[a], tokens[b])
                        where delta < MinimumEventDeltaE
                        select $"{a}~{b} ΔE {delta:F1}").ToList();

        // Assert
        tooClose.Should().BeEmpty("every event reads as its own colour, and pump never as matcha");
    }

    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    [InlineData("#767676", "#FFFFFF", 4.54)]
    public void ContrastRatio_PublishedExamples_Match(string a, string b, double expected)
    {
        // Arrange
        var (fg, bg) = (a, b);

        // Act
        var ratio = ColourMath.ContrastRatio(fg, bg);

        // Assert
        ratio.Should().BeApproximately(expected, 0.01);
    }

    [Fact]
    public void DeltaE2000_IdenticalAndOppositeColours_GiveZeroAndAboutOneHundred()
    {
        // Arrange
        var same = ColourMath.DeltaE2000("#5E7150", "#5E7150");

        // Act
        var extremes = ColourMath.DeltaE2000("#000000", "#FFFFFF");

        // Assert
        same.Should().Be(0);
        extremes.Should().BeApproximately(100, 0.5);
    }

    private static Palette Pick(string palette) => palette == "Night" ? WashiPalette.Night : WashiPalette.Day;

    /// <summary>Reads the base table and the care-event table of design-system.md §2.</summary>
    private static (Dictionary<string, string> Day, Dictionary<string, string> Night) ParseDocument(string document)
    {
        var day = new Dictionary<string, string>();
        var night = new Dictionary<string, string>();

        foreach (Match row in BaseRow().Matches(document))
        {
            var names = Codes().Matches(row.Groups["tokens"].Value).Select(m => m.Groups[1].Value).ToList();
            var dayValues = Hex().Matches(row.Groups["day"].Value).Select(m => m.Value).ToList();
            var nightValues = Hex().Matches(row.Groups["night"].Value).Select(m => m.Value).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                day[names[i]] = dayValues[i];
                night[names[i]] = nightValues[i];
            }
        }

        foreach (Match row in EventRow().Matches(document))
        {
            var name = row.Groups["event"].Value;
            var dayValues = Hex().Matches(row.Groups["day"].Value).Select(m => m.Value).ToList();
            var nightValues = Hex().Matches(row.Groups["night"].Value).Select(m => m.Value).ToList();
            string[] roles = ["Tint", "Mark", "Ink"];
            for (var i = 0; i < 3; i++)
            {
                day[name + roles[i]] = dayValues[i];
                night[name + roles[i]] = nightValues[i];
            }
        }

        return (day, night);
    }

    [GeneratedRegex(@"^\| (?<tokens>(`[A-Za-z0-9]+`( / )?)+) \| (?<day>[^|]*#[0-9A-F]{6}[^|]*) \| (?<night>[^|]*#[0-9A-F]{6}[^|]*) \|", RegexOptions.Multiline)]
    private static partial Regex BaseRow();

    [GeneratedRegex(@"^\| \*\*(?<event>Feed|Diaper|Sleep|Pump)\*\* \([a-z]+\) \| (?<day>[^|]+) \| (?<night>[^|]+) \|", RegexOptions.Multiline)]
    private static partial Regex EventRow();

    [GeneratedRegex("`([A-Za-z0-9]+)`")]
    private static partial Regex Codes();

    [GeneratedRegex("#[0-9A-F]{6}")]
    private static partial Regex Hex();
}
