using Coo.Mobile.Application.Insights;

namespace Coo.Mobile.Application.Tests;

/// <summary>The days side by side, noon to noon, and when her longest sleep usually starts.</summary>
public sealed class SleepPatternTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 11, 1);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>A nap 14:00–15:00, the night's first stretch 19:00–01:00 and a second 01:30–06:30, every day.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Nights(int count)
    {
        var sleeps = new List<(DateTimeOffset, DateTimeOffset)>();
        for (var d = 0; d < count; d++)
        {
            var day = Day1.AddDays(d);
            var next = day.AddDays(1);
            sleeps.Add((At(day, 14), At(day, 15)));
            sleeps.Add((At(day, 19), At(next, 1)));
            sleeps.Add((At(next, 1, 30), At(next, 6, 30)));
        }

        return sleeps;
    }

    [Fact]
    public void Build_FourNights_KeepsEachNightInOneColumnAndFindsItsUsualStart()
    {
        // Arrange
        var sleeps = Nights(4);
        var now = At(Day1.AddDays(4), 13);

        // Act
        var pattern = GetSleepPattern.Build(sleeps, [At(Day1, 22)], GetSleepPattern.LastEvening(now, Utc), 5, Utc, now);

        // Assert
        pattern.Days.Should().HaveCount(5);
        pattern.Days[0].Evening.Should().Be(Day1);
        var first = pattern.Days[0];
        first.Sleeps.Should().HaveCount(3);
        first.Sleeps.Single(s => s.IsLongest).From.Should().BeApproximately(7 / 24.0, 0.001);
        first.Feeds.Should().ContainSingle().Which.Should().BeApproximately(10 / 24.0, 0.001);
        pattern.UsualLongestStart.Should().Be(new TimeOnly(19, 0));
        pattern.UsualLongest.Should().Be(TimeSpan.FromHours(6));
        pattern.Days[^1].Logged.Should().BeFalse("nothing was logged after noon today");
    }

    [Fact]
    public void Build_TwoDaysOnly_GivesNoUsualStart()
    {
        // Arrange
        var now = At(Day1.AddDays(2), 13);

        // Act
        var pattern = GetSleepPattern.Build(Nights(2), [], GetSleepPattern.LastEvening(now, Utc), 7, Utc, now);

        // Assert
        pattern.UsualLongestStart.Should().BeNull();
        pattern.Days.Count(d => d.Logged).Should().Be(2);
    }

    [Fact]
    public void LastEvening_BeforeNoon_IsYesterdaysColumn()
    {
        // Act
        var evening = GetSleepPattern.LastEvening(At(Day1, 9), Utc);

        // Assert
        evening.Should().Be(Day1.AddDays(-1));
    }
}
