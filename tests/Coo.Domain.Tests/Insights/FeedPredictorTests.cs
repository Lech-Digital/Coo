using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>The next feed from her own feeds: by the hour, with her evening cluster feeds left without a time.</summary>
public sealed class FeedPredictorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 3, 2);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>Feeds at 7, 10, 13 and 16; a cluster at 18, 19 and 20; then 23:30 and 03:30 in the night.</summary>
    private static List<DateTimeOffset> Days(int count)
    {
        var feeds = new List<DateTimeOffset>();
        for (var d = 0; d < count; d++)
        {
            var day = Day1.AddDays(d);
            feeds.AddRange([At(day, 7), At(day, 10), At(day, 13), At(day, 16), At(day, 18), At(day, 19), At(day, 20), At(day, 23, 30), At(day.AddDays(1), 3, 30)]);
        }

        return feeds;
    }

    [Fact]
    public void Predict_AfterANightFeed_UsesTheNightIntervals()
    {
        // Arrange: the 03:30 feed of the sixth night has just started.
        var feeds = Days(6);

        // Act
        var next = FeedPredictor.Predict(feeds, At(Day1.AddDays(6), 3, 35), Utc);

        // Assert: 3:30 to 7:00, not the day's three hours.
        next.Should().NotBeNull();
        next!.Typical.Should().Be(TimeSpan.FromHours(3.5));
        next.Cluster.Should().BeFalse();
    }

    [Fact]
    public void Predict_MidMorning_UsesTheDayIntervals()
    {
        // Arrange
        var feeds = Days(6);
        var today = Day1.AddDays(6);
        feeds.AddRange([At(today, 7), At(today, 10)]);

        // Act
        var next = FeedPredictor.Predict(feeds, At(today, 10, 5), Utc);

        // Assert
        next.Should().NotBeNull();
        next!.Typical.Should().Be(TimeSpan.FromHours(3));
        next.Earliest.Should().BeBefore(At(today, 13));
        next.Latest.Should().BeAfter(At(today, 13));
    }

    [Fact]
    public void Predict_InHerEveningCluster_SaysCluster()
    {
        // Arrange
        var feeds = Days(6);
        var today = Day1.AddDays(6);
        feeds.AddRange([At(today, 7), At(today, 10), At(today, 13), At(today, 16), At(today, 18)]);

        // Act
        var next = FeedPredictor.Predict(feeds, At(today, 18, 10), Utc);

        // Assert
        next.Should().NotBeNull();
        next!.Cluster.Should().BeTrue();
    }

    [Fact]
    public void Predict_TwoDaysOfFeeds_ReturnsNull()
    {
        // Arrange: too few days to learn from.
        var feeds = Days(1);

        // Act
        var next = FeedPredictor.Predict(feeds, At(Day1.AddDays(1), 8), Utc);

        // Assert
        next.Should().BeNull();
    }

    [Fact]
    public void Sessions_OtherSideTenMinutesLater_IsOneFeed()
    {
        // Act
        var sessions = FeedPredictor.Sessions([At(Day1, 7), At(Day1, 7, 10), At(Day1, 10)]);

        // Assert
        sessions.Should().Equal(At(Day1, 7), At(Day1, 10));
    }
}
