using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>Proving the predictor on the baby's own log: the windows Today showed, against when she really slept.</summary>
public sealed class SleepPredictionCheckTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Fact]
    public void Replay_FourWeeksOfASteadyBaby_CountsTheShownWindowsAndMostlyHits()
    {
        // Arrange: the backtest's steady 5-month baby, four weeks logged.
        var baby = SleepPredictorBacktest.Babies[3];
        var (_, logged) = SyntheticBaby.Generate(baby, Amsterdam);
        var now = logged[^1].End!.Value.AddHours(1);
        var ageNow = baby.AgeDays + baby.Days;

        // Act
        var record = SleepPredictionCheck.Replay(logged, ageNow, now, Amsterdam);

        // Assert
        record.IsEnough.Should().BeTrue();
        record.Shown.Should().BeGreaterThan(20, "two weeks of naps and bedtimes, each predicted at the wake-up before");
        record.HitRate.Should().BeGreaterThanOrEqualTo(0.6, "the release target of ADR 0008 §6");
        record.MedianError.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Replay_OnlyOldSleeps_CountsNothingOutsideTheFortnight()
    {
        // Arrange
        var baby = SleepPredictorBacktest.Babies[3];
        var (_, logged) = SyntheticBaby.Generate(baby, Amsterdam);
        var now = logged[^1].End!.Value.AddDays(20);

        // Act
        var record = SleepPredictionCheck.Replay(logged, baby.AgeDays + baby.Days + 20, now, Amsterdam);

        // Assert
        record.Shown.Should().Be(0);
        record.IsEnough.Should().BeFalse();
        record.HitRate.Should().Be(0);
    }

    [Fact]
    public void Replay_ADayWithAnUnloggedNap_DoesNotCountTheGapAsAMiss()
    {
        // Arrange: steady 6-month days (wake 07:00, naps 09:00, 12:30 and 16:00, bed 19:00), then a day whose midday
        // nap nobody logged, so the window after the morning nap is followed by the late-afternoon nap.
        var sleeps = new List<SleepSpan>();
        var first = new DateTime(2026, 10, 1);
        for (var d = 0; d < 6; d++)
        {
            var day = first.AddDays(d);
            DateTimeOffset At(double h) => new(day.AddHours(h), Amsterdam.GetUtcOffset(day.AddHours(h)));
            sleeps.Add(new SleepSpan(At(9), At(10)));
            if (d != 5)
                sleeps.Add(new SleepSpan(At(12.5), At(13.5)));
            sleeps.Add(new SleepSpan(At(16), At(16.5)));
            sleeps.Add(new SleepSpan(At(19), At(31)));
        }

        var now = new DateTimeOffset(first.AddDays(6).AddHours(8), TimeSpan.FromHours(2));

        // Act
        var record = SleepPredictionCheck.Replay(sleeps, 180, now, Amsterdam);

        // Assert: seven windows shown on the last two days; the last day's midday one (expecting about 12:30, the next
        // logged sleep at 16:00) is a gap, not counted. The one after it counts the day's naps one short and misses
        // bedtime, as the parent would have seen it.
        record.Shown.Should().Be(6);
        record.Hits.Should().Be(5);
    }
}
