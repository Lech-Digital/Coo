using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Tests;

/// <summary>This week against the one before, per logged day.</summary>
public sealed class WeekComparisonTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static List<DayCare> Week(int daysAgo, double sleepHours, int feeds, int wet, int days = 7, int diapers = -1) =>
        [.. Enumerable.Range(0, days).Select(i => new DayCare(
            Today.AddDays(-daysAgo - i), true, TimeSpan.FromHours(sleepHours), 5, TimeSpan.FromHours(4), feeds, feeds, TimeSpan.Zero, 0,
            diapers < 0 ? wet + 1 : diapers, wet, 1, 0))];

    [Fact]
    public void Of_TwoFullWeeks_GivesTheDifferencesPerDay()
    {
        // Act
        var week = WeekComparison.Of(Week(1, 14, 9, 6), Week(8, 15, 7, 6));

        // Assert
        week.Compared.Should().BeTrue();
        week.Sleep.Should().Be(TimeSpan.FromHours(-1));
        week.Feeds.Should().Be(2);
        week.WetPerDay.Should().Be(6);
    }

    [Fact]
    public void Of_ThreeDaysThisWeek_ComparesNothing()
    {
        // Act
        var week = WeekComparison.Of(Week(1, 14, 9, 6, days: 3), Week(8, 15, 7, 6));

        // Assert
        week.HasThisWeek.Should().BeFalse();
        week.Compared.Should().BeFalse();
    }

    [Fact]
    public void Of_NoDiapersLogged_DoesNotCountDryDays()
    {
        // Act: diapers not logged at all this week.
        var week = WeekComparison.Of(Week(1, 14, 9, 0, diapers: 0), []);

        // Assert
        double.IsNaN(week.WetPerDay).Should().BeTrue();
        week.Compared.Should().BeFalse();
    }
}
