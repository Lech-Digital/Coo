using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Tests;

public sealed class AppearanceRuleTests
{
    private static readonly QuietHours Default = QuietHours.Default;
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Theory]
    [InlineData("21:59", false, PaletteKind.Day)]
    [InlineData("22:00", false, PaletteKind.Night)]
    [InlineData("03:12", false, PaletteKind.Night)]
    [InlineData("06:59", false, PaletteKind.Night)]
    [InlineData("07:00", false, PaletteKind.Day)]
    [InlineData("14:00", true, PaletteKind.Night)]
    public void Resolve_AutoAtBoundaries_FollowsQuietHoursOrDarkMode(string time, bool osDark, PaletteKind expected)
    {
        // Arrange
        var local = TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        var kind = AppearanceRule.Resolve(AppearanceMode.Auto, local, Default, osDark);

        // Assert
        kind.Should().Be(expected);
    }

    [Theory]
    [InlineData(AppearanceMode.Day, "03:00", true, PaletteKind.Day)]
    [InlineData(AppearanceMode.Night, "14:00", false, PaletteKind.Night)]
    public void Resolve_FixedMode_IgnoresTimeAndDarkMode(AppearanceMode mode, string time, bool osDark, PaletteKind expected)
    {
        // Arrange
        var local = TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

        // Act
        var kind = AppearanceRule.Resolve(mode, local, Default, osDark);

        // Assert
        kind.Should().Be(expected);
    }

    [Fact]
    public void Contains_QuietHoursWithinOneDay_DoNotWrapMidnight()
    {
        // Arrange: a nap-time quiet window.
        var afternoon = new QuietHours(new TimeOnly(13, 0), new TimeOnly(15, 0));

        // Act
        var (before, inside, after) = (afternoon.Contains(new(12, 59)), afternoon.Contains(new(13, 0)), afternoon.Contains(new(15, 0)));

        // Assert
        (before, inside, after).Should().Be((false, true, false));
    }

    [Fact]
    public void Contains_EqualStartAndEnd_MeansNoQuietHours()
    {
        // Arrange
        var none = new QuietHours(new TimeOnly(22, 0), new TimeOnly(22, 0));

        // Act
        var contains = none.Contains(new TimeOnly(22, 0));

        // Assert
        contains.Should().BeFalse();
    }

    [Fact]
    public void NextChange_EveningBeforeQuietHours_ReturnsTodaysStart()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.FromHours(2));

        // Act
        var next = AppearanceRule.NextChange(AppearanceMode.Auto, now, Default, Amsterdam);

        // Assert
        next.Should().Be(new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void NextChange_AtNight_ReturnsTomorrowMorningsEnd()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.FromHours(2));

        // Act
        var next = AppearanceRule.NextChange(AppearanceMode.Auto, now, Default, Amsterdam);

        // Assert
        next.Should().Be(new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void NextChange_AcrossTheAutumnClockChange_UsesTheNewOffset()
    {
        // Arrange: the night of 24–25 October 2026; 07:00 on the 25th is CET (+01:00).
        var now = new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.FromHours(2));

        // Act
        var next = AppearanceRule.NextChange(AppearanceMode.Auto, now, Default, Amsterdam);

        // Assert
        next.Should().Be(new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void NextChange_FixedMode_ReturnsNull()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.FromHours(2));

        // Act
        var next = AppearanceRule.NextChange(AppearanceMode.Night, now, Default, Amsterdam);

        // Assert
        next.Should().BeNull();
    }
}
