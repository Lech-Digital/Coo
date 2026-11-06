using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using FluentAssertions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The two lines under each of the Log's totals: what the number is made of, and how it compares.</summary>
[Collection(nameof(StringsState))]
public sealed class LogTotalsTests : IDisposable
{
    private static readonly Formats Ml = new(true, VolumeUnit.Millilitres);
    private static readonly DateOnly Shown = new(2026, 11, 5);
    private static readonly Dictionary<string, string> NoLast = [];

    public LogTotalsTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static DayCare Day(DateOnly date, int feeds = 0, int breast = 0, int bottles = 0, decimal bottleMl = 0, int wet = 0, int dirty = 0, bool logged = true) =>
        new(date, logged, TimeSpan.Zero, 0, TimeSpan.Zero, feeds, breast, TimeSpan.Zero, bottleMl, wet + dirty, wet, dirty, 0, bottles);

    [Fact]
    public void For_FeedsTodayWithAWeekBefore_SplitsAndComparesWithTheLastFeed()
    {
        // Arrange
        var day = Day(Shown, feeds: 3, breast: 2, bottles: 1, bottleMl: 90);
        var before = new[] { Day(Shown.AddDays(-1), feeds: 6), Day(Shown.AddDays(-2), feeds: 8) };
        var last = new Dictionary<string, string> { ["feeding"] = "09:52" };

        // Act
        var (detail, compare) = LogTotals.For("feeding", day, before, 0, last, Ml);

        // Assert
        detail.Should().Be("Breast 2 · bottle 1, 90 ml");
        compare.Should().Be("Last 09:52 · usually\u00A07");
    }

    [Fact]
    public void For_OnlyBreastFeeds_LeavesTheBottlesOut()
    {
        // Arrange
        var day = Day(Shown, feeds: 3, breast: 3);

        // Act
        var (detail, _) = LogTotals.For("feeding", day, [], 0, NoLast, Ml);

        // Assert
        detail.Should().Be("Breast 3");
    }

    [Fact]
    public void For_OnlyUnloggedDaysBefore_HasNoUsual()
    {
        // Arrange
        var day = Day(Shown, wet: 3, dirty: 1);
        var before = new[] { Day(Shown.AddDays(-1), logged: false) };

        // Act
        var (detail, compare) = LogTotals.For("diaper", day, before, 0, NoLast, Ml);

        // Assert
        detail.Should().Be("Wet 3 · dirty 1");
        compare.Should().BeEmpty();
    }

    [Fact]
    public void For_NothingCountedOnAPastDay_ComparesWithoutDetail()
    {
        // Arrange
        var before = new[] { Day(Shown.AddDays(-1), wet: 5, dirty: 1) };

        // Act
        var (detail, compare) = LogTotals.For("diaper", null, before, 0, NoLast, Ml);

        // Assert
        detail.Should().BeEmpty();
        compare.Should().Be("Usually\u00A06");
    }

    [Fact]
    public void For_NoPumpedMilkBefore_HasNoUsualVolume()
    {
        // Arrange
        var before = new[] { Day(Shown.AddDays(-1), feeds: 2) };

        // Act
        var (detail, compare) = LogTotals.For("pump", null, before, 0, NoLast, Ml);

        // Assert
        detail.Should().BeEmpty();
        compare.Should().BeEmpty();
    }

    [Fact]
    public void For_TodayComparedByNow_SaysByNow()
    {
        // Arrange: the days before counted up to the same time of day.
        var day = Day(Shown, feeds: 3, breast: 3);
        var before = new[] { Day(Shown.AddDays(-1), feeds: 3), Day(Shown.AddDays(-2), feeds: 3) };
        var last = new Dictionary<string, string> { ["feeding"] = "09:52" };

        // Act
        var (_, compare) = LogTotals.For("feeding", day, before, 0, last, Ml, byNow: true);

        // Assert
        compare.Should().Be("Last 09:52 · usually 3 by\u00A0now");
    }

    [Fact]
    public void For_ByNowWithoutALastTime_SaysUsuallyByNow()
    {
        // Arrange
        var before = new[] { Day(Shown.AddDays(-1), wet: 2) };

        // Act
        var (_, compare) = LogTotals.For("diaper", null, before, 0, NoLast, Ml, byNow: true);

        // Assert
        compare.Should().Be("Usually 2 by\u00A0now");
    }
}
