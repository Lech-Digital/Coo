using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

public sealed class SleepPredictorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 3, 2);
    private const int SixMonths = 183;

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>Nights 19:00–07:00 and one nap a day starting <paramref name="window"/> after waking, lasting an hour.</summary>
    private static List<SleepSpan> Days(int count, TimeSpan window, TimeSpan? napLength = null)
    {
        var sleeps = new List<SleepSpan>();
        for (var d = 0; d < count; d++)
        {
            var day = Day1.AddDays(d);
            var napStart = At(day, 7) + window;
            sleeps.Add(new SleepSpan(napStart, napStart + (napLength ?? TimeSpan.FromHours(1))));
            sleeps.Add(new SleepSpan(At(day, 19), At(day.AddDays(1), 7)));
        }

        return sleeps;
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(9, 6, true)]
    [InlineData(2, 2, false)]
    [InlineData(5, 2, false)]
    public void IsPersonal_OwnWindowsOnDays_NeedsThreeOfEach(int observations, int days, bool expected)
    {
        // Arrange: a low-confidence prediction (a young baby's windows vary widely) is still hers once there are enough.
        var prediction = new SleepPrediction(
            2, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            PredictionConfidence.Low, TimeSpan.FromMinutes(80), TimeSpan.FromMinutes(80), days, observations,
            WakeWindowPrior.For(61), false, SleepPredictor.Version);

        // Act
        var personal = prediction.IsPersonal;

        // Assert
        Assert.Equal(expected, personal);
    }

    [Fact]
    public void Predict_NoSleepsLogged_ReturnsNull()
    {
        // Act
        var prediction = SleepPredictor.Predict([], SixMonths, At(Day1, 9), Utc);

        // Assert
        prediction.Should().BeNull();
    }

    [Fact]
    public void Predict_BabyAsleep_ReturnsNull()
    {
        // Arrange
        var sleeps = Days(3, TimeSpan.FromHours(2));
        sleeps.Add(new SleepSpan(At(Day1.AddDays(3), 9), null));

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, At(Day1.AddDays(3), 9, 30), Utc);

        // Assert
        prediction.Should().BeNull("v1 does not predict when a sleep ends");
    }

    [Fact]
    public void Predict_AwakeInTheNight_ReturnsNull()
    {
        // Arrange: woke at 02:00 from a night sleep
        var sleeps = new List<SleepSpan> { new(At(Day1, 19), At(Day1.AddDays(1), 2)) };

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, At(Day1.AddDays(1), 2, 10), Utc);

        // Assert
        prediction.Should().BeNull("nobody wants a nap suggested at 2 AM");
    }

    [Fact]
    public void Predict_OnlyOneNight_UsesTheAgePriorWithLowConfidence()
    {
        // Arrange
        var sleeps = new List<SleepSpan> { new(At(Day1, 19), At(Day1.AddDays(1), 7)) };

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, At(Day1.AddDays(1), 7, 5), Utc)!;

        // Assert
        prediction.Position.Should().Be(1);
        prediction.Estimate.Should().Be(TimeSpan.FromHours(2), "the first window of a six-month-old starts at the low end");
        prediction.PersonalMedian.Should().BeNull();
        prediction.Confidence.Should().Be(PredictionConfidence.Low);
        prediction.Version.Should().Be(SleepPredictor.Version);
    }

    [Fact]
    public void Predict_TwoWeeksOfSteadyWindows_LearnsThisBabysRhythmWithHighConfidence()
    {
        // Arrange: this baby naps 2 h 30 after waking, half an hour later than the prior's 2 h
        var sleeps = Days(14, TimeSpan.FromMinutes(150));
        var now = At(Day1.AddDays(14), 7, 5);

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, now, Utc)!;

        // Assert
        prediction.PersonalMedian.Should().Be(TimeSpan.FromMinutes(150));
        prediction.Estimate.Should().BeGreaterThan(TimeSpan.FromMinutes(140)).And.BeLessThan(TimeSpan.FromMinutes(150));
        prediction.Confidence.Should().Be(PredictionConfidence.High);
        prediction.Earliest.Should().Be(prediction.Centre - TimeSpan.FromMinutes(10), "no spread: the band's minimum");
        prediction.DaysUsed.Should().Be(13, "the first day is just over 14 days ago");
    }

    [Fact]
    public void Predict_LastNapWasShort_ShortensTheWindow()
    {
        // Arrange: a 30-minute first nap today
        var sleeps = Days(14, TimeSpan.FromMinutes(150));
        var today = Day1.AddDays(14);
        sleeps.Add(new SleepSpan(At(today, 9, 30), At(today, 10)));

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, At(today, 10, 5), Utc)!;

        // Assert
        prediction.Position.Should().Be(2);
        prediction.ShortNapAdjusted.Should().BeTrue();
        prediction.Estimate.Should().Be(prediction.Prior.At(2) * 0.85);
    }

    [Fact]
    public void Predict_NapsNoLongerLoggedForADay_LosesThePositionAndCountsFromTheMorning()
    {
        // Arrange: no night logged, then two naps today
        var today = Day1.AddDays(5);
        var sleeps = new List<SleepSpan>
        {
            new(At(Day1, 10), At(Day1, 11)),
            new(At(today, 9), At(today, 10)),
            new(At(today, 13), At(today, 14)),
        };

        // Act
        var prediction = SleepPredictor.Predict(sleeps, SixMonths, At(today, 14, 5), Utc)!;

        // Assert
        prediction.Position.Should().Be(3);
    }

    [Fact]
    public void Classify_PersonLabelledANap_BeatsTheLength()
    {
        // Arrange
        var sleeps = new List<SleepSpan> { new(At(Day1, 13), At(Day1, 19), SleepKindOverride.Nap) };

        // Act
        var classes = SleepClassifier.Classify(sleeps, NightWindow.Default, Utc);

        // Assert
        classes.Should().Equal(SleepClass.Nap);
    }

    [Fact]
    public void Classify_BrokenNight_KeepsTheFragmentsAsNight()
    {
        // Arrange: 19:00–22:30, awake 40 minutes, 23:10–02:00
        var sleeps = new List<SleepSpan>
        {
            new(At(Day1, 19), At(Day1, 22, 30)),
            new(At(Day1, 23, 10), At(Day1.AddDays(1), 2)),
        };

        // Act
        var classes = SleepClassifier.Classify(sleeps, NightWindow.Default, Utc);

        // Assert
        classes.Should().Equal(SleepClass.Night, SleepClass.Night);
    }

    [Theory]
    [InlineData(20, 0, 9 * 60, null, SleepClass.Night)]
    [InlineData(13, 0, 90, null, SleepClass.Nap)]
    [InlineData(1, 30, 200, null, SleepClass.Night)]
    [InlineData(19, 30, 40, null, SleepClass.Nap)]
    [InlineData(14, 0, 6 * 60, null, SleepClass.Night)]
    [InlineData(21, 0, 10 * 60, SleepKindOverride.Nap, SleepClass.Nap)]
    public void ClassifyOne_DecideForMe_FollowsLengthAndTimeOfDay(int hour, int minute, int minutes, SleepKindOverride? label, SleepClass expected)
    {
        // Arrange
        var start = At(Day1, hour, minute);
        var sleep = new SleepSpan(start, start.AddMinutes(minutes), label);

        // Act
        var result = SleepClassifier.ClassifyOne(sleep, new TimeOnly(hour, minute));

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ClassifyOne_RunningSinceNineInTheEvening_IsNight()
    {
        // Act
        var result = SleepClassifier.ClassifyOne(new SleepSpan(At(Day1, 21, 0), null), new TimeOnly(21, 0));

        // Assert
        result.Should().Be(SleepClass.Night);
    }

    [Fact]
    public void LearnNightWindow_FewerThanThreeNights_IsTheDefault()
    {
        // Arrange
        var sleeps = Days(2, TimeSpan.FromHours(2));

        // Act
        var window = SleepClassifier.LearnNightWindow(sleeps, Utc);

        // Assert
        window.Should().Be(NightWindow.Default);
    }

    [Fact]
    public void LearnNightWindow_LateNights_RunsAcrossMidnight()
    {
        // Arrange: nights starting 23:30, 00:00 and 00:30
        var sleeps = new List<SleepSpan>
        {
            new(At(Day1, 23, 30), At(Day1.AddDays(1), 8)),
            new(At(Day1.AddDays(2), 0), At(Day1.AddDays(2), 8)),
            new(At(Day1.AddDays(3), 0, 30), At(Day1.AddDays(3), 8)),
        };

        // Act
        var window = SleepClassifier.LearnNightWindow(sleeps, Utc);

        // Assert
        window.Should().Be(new NightWindow(new TimeOnly(23, 30), new TimeOnly(0, 30)));
        window.Contains(new TimeOnly(0, 10)).Should().BeTrue();
    }

    [Theory]
    [InlineData(10, 35, 60)]
    [InlineData(60, 60, 90)]
    [InlineData(183, 120, 180)]
    [InlineData(600, 240, 360)]
    public void WakeWindowPrior_ForAge_IsThatBandsRange(int days, int lowMinutes, int highMinutes)
    {
        // Act
        var band = WakeWindowPrior.For(days);

        // Assert
        band.Low.Should().Be(TimeSpan.FromMinutes(lowMinutes));
        band.High.Should().Be(TimeSpan.FromMinutes(highMinutes));
        band.At(band.TypicalNaps + 1).Should().Be(band.High, "the window before bedtime is the longest");
    }
}
