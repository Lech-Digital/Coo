using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>Back to sleep after a night waking: from her own wakings, weighted by how alike they are to tonight's.</summary>
public sealed class NightWakingPredictorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 3, 2);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>
    /// Nights with two wakings: 23:00, awake 30 m, a feed 23:00–23:20 (settled 10 m after it); and 03:00, awake 40 m, a
    /// feed 03:05–03:25 (settled 15 m after it). A nap at 10:00 each day.
    /// </summary>
    private static (List<SleepSpan> Sleeps, List<FeedSpan> Feeds) Nights(int count)
    {
        var sleeps = new List<SleepSpan>();
        var feeds = new List<FeedSpan>();
        for (var d = 0; d < count; d++)
        {
            var day = Day1.AddDays(d);
            var next = day.AddDays(1);
            sleeps.Add(new SleepSpan(At(day, 10), At(day, 11)));
            sleeps.Add(new SleepSpan(At(day, 19), At(day, 23)));
            feeds.Add(new FeedSpan(At(day, 23), At(day, 23, 20)));
            sleeps.Add(new SleepSpan(At(day, 23, 30), At(next, 3)));
            feeds.Add(new FeedSpan(At(next, 3, 5), At(next, 3, 25)));
            sleeps.Add(new SleepSpan(At(next, 3, 40), At(next, 7)));
        }

        return (sleeps, feeds);
    }

    /// <summary>Tonight so far: the nap, and the first stretch ending at 23:00.</summary>
    private static List<SleepSpan> Tonight(List<SleepSpan> history, DateOnly day) =>
        [.. history, new SleepSpan(At(day, 10), At(day, 11)), new SleepSpan(At(day, 19), At(day, 23))];

    [Fact]
    public void Predict_AwakeAtElevenWithoutAFeed_UsesTheWakingsMostLikeIt()
    {
        // Arrange: five nights; tonight she woke at 23:00 after the same four-hour stretch.
        var (sleeps, feeds) = Nights(5);
        var tonight = Day1.AddDays(5);

        // Act
        var back = NightWakingPredictor.Predict(Tonight(sleeps, tonight), feeds, At(tonight, 23, 5), Utc);

        // Assert: the 23:00 wakings (30 m) count, not the longer ones near morning (40 m).
        back.Should().NotBeNull();
        back!.Typical.Should().Be(TimeSpan.FromMinutes(30));
        back.FromFeed.Should().BeFalse();
        back.Latest.Should().Be(At(tonight, 23, 40), "her wakings spread over 10 minutes");
    }

    [Fact]
    public void Predict_FeedEnded_CountsFromTheEndOfTheFeed()
    {
        // Arrange: tonight's feed took longer than usual, 23:00–23:35.
        var (sleeps, feeds) = Nights(5);
        var tonight = Day1.AddDays(5);
        feeds.Add(new FeedSpan(At(tonight, 23), At(tonight, 23, 35)));

        // Act
        var back = NightWakingPredictor.Predict(Tonight(sleeps, tonight), feeds, At(tonight, 23, 36), Utc);

        // Assert: settled 10 m after a feed at this hour, so about 23:45.
        back.Should().NotBeNull();
        back!.FromFeed.Should().BeTrue();
        back.Typical.Should().Be(TimeSpan.FromMinutes(10));
        back.Earliest.Should().BeOnOrBefore(At(tonight, 23, 45));
        back.Latest.Should().BeOnOrAfter(At(tonight, 23, 45));
    }

    [Fact]
    public void Predict_FeedRunning_GivesTheSettleTimeWithoutAClockTime()
    {
        // Arrange
        var (sleeps, feeds) = Nights(5);
        var tonight = Day1.AddDays(5);
        feeds.Add(new FeedSpan(At(tonight, 23, 2), null));

        // Act
        var back = NightWakingPredictor.Predict(Tonight(sleeps, tonight), feeds, At(tonight, 23, 10), Utc);

        // Assert
        back.Should().NotBeNull();
        back!.FeedRunning.Should().BeTrue();
        back.Earliest.Should().BeNull();
    }

    [Fact]
    public void Predict_TwoNightsOnly_ReturnsNull()
    {
        // Arrange: no age table at night, so nothing until three nights of her own.
        var (sleeps, feeds) = Nights(2);
        var tonight = Day1.AddDays(2);

        // Act
        var back = NightWakingPredictor.Predict(Tonight(sleeps, tonight), feeds, At(tonight, 23, 5), Utc);

        // Assert
        back.Should().BeNull();
    }

    [Fact]
    public void Predict_AwakeAfterANap_ReturnsNull()
    {
        // Arrange: the day, after the nap.
        var (sleeps, feeds) = Nights(5);
        var today = Day1.AddDays(5);
        List<SleepSpan> sofar = [.. sleeps, new SleepSpan(At(today, 10), At(today, 11))];

        // Act
        var back = NightWakingPredictor.Predict(sofar, feeds, At(today, 11, 30), Utc);

        // Assert
        back.Should().BeNull();
    }
}
