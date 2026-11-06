using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>Naming a nap transition from this week against the one before.</summary>
public sealed class NapTransitionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 3, 2);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>Days of <paramref name="naps"/> naps from 9:00, two and a half hours apart, and a night from 19:00.</summary>
    private static IEnumerable<SleepSpan> Day(DateOnly day, int naps)
    {
        for (var n = 0; n < naps; n++)
        {
            var start = At(day, 9).AddMinutes(150 * n);
            yield return new SleepSpan(start, start.AddMinutes(60));
        }

        yield return new SleepSpan(At(day, 19), At(day.AddDays(1), 7));
    }

    private static List<SleepSpan> History(params int[] napsPerDay) =>
        [.. napsPerDay.SelectMany((n, d) => Day(Day1.AddDays(d), n))];

    [Fact]
    public void Detect_FourNapsThenMostlyThree_SaysSettledOnThree()
    {
        // Arrange: a week of 4 naps, then 5 days of 3 and 2 of 4.
        var sleeps = History(4, 4, 4, 4, 4, 4, 4, 3, 4, 3, 3, 4, 3, 3);

        // Act
        var transition = NapTransitions.Detect(sleeps, At(Day1.AddDays(14), 8), Utc);

        // Assert
        transition.Should().Be(new NapTransition(4, 3, Settled: true));
    }

    [Fact]
    public void Detect_TwoShortDaysOnly_SaysOnItsWay()
    {
        // Arrange
        var sleeps = History(4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 4, 4, 3, 4);

        // Act
        var transition = NapTransitions.Detect(sleeps, At(Day1.AddDays(14), 8), Utc);

        // Assert
        transition.Should().Be(new NapTransition(4, 3, Settled: false));
    }

    [Fact]
    public void Detect_SteadyNaps_ReturnsNull()
    {
        // Arrange
        var sleeps = History(3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3);

        // Act
        var transition = NapTransitions.Detect(sleeps, At(Day1.AddDays(14), 8), Utc);

        // Assert
        transition.Should().BeNull();
    }

    [Fact]
    public void Detect_ThreeDaysLogged_ReturnsNull()
    {
        // Act
        var transition = NapTransitions.Detect(History(4, 3, 3), At(Day1.AddDays(3), 8), Utc);

        // Assert
        transition.Should().BeNull();
    }
}
