using Coo.Domain.Insights;

namespace Coo.Domain.Tests.Insights;

/// <summary>Spec 019 §4: the sleep planner's day, its safety checks, the check-in's measures and its one suggestion.</summary>
public sealed class SleepPlannerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day1 = new(2026, 10, 1);
    private static readonly SleepPlanSafety Safe = new(false, false);

    private static SleepPlanIntake Intake(SettlingApproach approach = SettlingApproach.Fading, params SleepPlanGoal[] goals) =>
        new(goals.ToHashSet(), approach);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>Nights 19:30–06:45 (one waking at 02:00 for 20 minutes when asked) and two naps of <paramref name="nap"/>.</summary>
    private static List<SleepSpan> Days(int count, TimeSpan nap, bool waking = false, int startDay = 0)
    {
        var sleeps = new List<SleepSpan>();
        for (var d = startDay; d < startDay + count; d++)
        {
            var day = Day1.AddDays(d);
            sleeps.Add(new SleepSpan(At(day, 9, 30), At(day, 9, 30) + nap));
            sleeps.Add(new SleepSpan(At(day, 14), At(day, 14) + nap));
            if (waking)
            {
                sleeps.Add(new SleepSpan(At(day, 19, 30), At(day.AddDays(1), 2)));
                sleeps.Add(new SleepSpan(At(day.AddDays(1), 2, 20), At(day.AddDays(1), 6, 45)));
            }
            else
            {
                sleeps.Add(new SleepSpan(At(day, 19, 30), At(day.AddDays(1), 6, 45)));
            }
        }

        return sleeps;
    }

    [Fact]
    public void Plan_UnderTwoWeeks_FailsTooYoung()
    {
        // Act
        var result = SleepPlanner.Plan(Intake(), Safe, [], 10, At(Day1, 12), Utc);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(SleepPlanErrors.TooYoung, result.Error!.Code);
    }

    [Theory]
    [InlineData(true, false, SleepPlanErrors.Unwell)]
    [InlineData(false, true, SleepPlanErrors.SeeDoctor)]
    [InlineData(true, true, SleepPlanErrors.SeeDoctor)]
    public void Plan_HealthWorries_PlansNothing(bool unwell, bool concern, string expected)
    {
        // Act
        var result = SleepPlanner.Plan(Intake(), new SleepPlanSafety(unwell, concern), [], 200, At(Day1, 12), Utc);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public void Plan_SixMonthsNoLog_TypicalDayFromSevenWithThreeNaps()
    {
        // Act
        var day = SleepPlanner.Plan(Intake(), Safe, [], 183, At(Day1, 12), Utc).Value;

        // Assert
        Assert.Equal(new TimeOnly(7, 0), day.WakeUp);
        Assert.Equal(3, day.Naps.Count);
        Assert.All(day.Naps, n => Assert.InRange(n.WindowBefore, day.Windows.Low, day.Windows.High));
        Assert.InRange(day.LastWindow, day.Windows.Low, day.Windows.High);
        var end = day.Naps[^1].Start.Add(day.Naps[^1].Length).Add(day.LastWindow);
        Assert.Equal(end, day.Bedtime);
        Assert.DoesNotContain(PlanNote.OwnWindows, day.Notes);
    }

    [Fact]
    public void Plan_UnderFourMonthsWithATrainingApproach_IsRhythmOnly()
    {
        // Act
        var day = SleepPlanner.Plan(Intake(SettlingApproach.CheckAndConsole), Safe, [], 70, At(Day1, 12), Utc).Value;

        // Assert
        Assert.Contains(PlanNote.RhythmOnlyUnderFourMonths, day.Notes);
        Assert.Equal(SettlingApproach.Rhythm, SleepPlanner.ApproachFor(SettlingApproach.CheckAndConsole, 70));
        Assert.Equal(SettlingApproach.Chair, SleepPlanner.ApproachFor(SettlingApproach.Chair, 130));
    }

    [Theory]
    [InlineData(250, 2)]
    [InlineData(380, 1)]
    public void Plan_DropANap_DropsOnlyWhereHerAgeAllows(int ageDays, int expectedNaps)
    {
        // Act: at 8 months two naps are the fewest typical; at a year one is.
        var day = SleepPlanner.Plan(Intake(SettlingApproach.Fading, SleepPlanGoal.DropANap), Safe, [], ageDays, At(Day1, 12), Utc).Value;

        // Assert
        Assert.Equal(expectedNaps, day.Naps.Count);
        Assert.Equal(expectedNaps < SleepNorms.For(ageDays).Naps, day.Notes.Contains(PlanNote.NapDropped));
    }

    [Fact]
    public void Plan_HerOwnLog_UsesHerMorningWindowsAndNaps()
    {
        // Arrange: two weeks of a steady 8-month-old: up at 06:45, naps at 09:30 and 14:00 of 75 minutes.
        var sleeps = Days(14, TimeSpan.FromMinutes(75));

        // Act
        var day = SleepPlanner.Plan(Intake(), Safe, sleeps, 250, At(Day1.AddDays(14), 12), Utc).Value;

        // Assert
        Assert.Equal(new TimeOnly(6, 45), day.WakeUp);
        Assert.Contains(PlanNote.OwnWindows, day.Notes);
        Assert.Contains(PlanNote.OwnNaps, day.Notes);
        Assert.All(day.Naps, n => Assert.Equal(TimeSpan.FromMinutes(75), n.Length));
        Assert.All(day.Naps, n => Assert.InRange(n.WindowBefore, day.Windows.Low, day.Windows.High));
    }

    [Theory]
    [InlineData(20, 1UL)]
    [InlineData(60, 2UL)]
    [InlineData(130, 3UL)]
    [InlineData(200, 4UL)]
    [InlineData(300, 5UL)]
    [InlineData(400, 6UL)]
    [InlineData(500, 7UL)]
    public void Plan_SyntheticBabies_EveryWindowWithinHerAgeAndNapsAsTypical(int ageDays, ulong seed)
    {
        // Arrange: three weeks logged by a family that misses some naps and rounds times.
        var (_, logged) = SyntheticBaby.Generate(new BabyTraits(ageDays - 21, 21, 1.1, 20, 0.1, 0.2, seed), Utc);
        var now = logged[^1].End!.Value + TimeSpan.FromHours(1);

        // Act
        var day = SleepPlanner.Plan(Intake(), Safe, logged, ageDays, now, Utc).Value;

        // Assert
        Assert.Equal(SleepNorms.For(ageDays).Naps, day.Naps.Count);
        Assert.All(day.Naps, n => Assert.InRange(n.WindowBefore, day.Windows.Low - TimeSpan.FromMinutes(5), day.Windows.High + TimeSpan.FromMinutes(5)));
        Assert.All(day.Naps, n => Assert.InRange(n.Length, TimeSpan.FromMinutes(20), TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Measure_NightsWithOneWaking_CountsWakingsLongestStretchAndBedtime()
    {
        // Arrange
        var sleeps = Days(7, TimeSpan.FromMinutes(60), waking: true);

        // Act
        var m = SleepPlanner.Measure(sleeps, At(Day1, 0), At(Day1.AddDays(7), 8), Utc);

        // Assert
        Assert.Equal(7, m.Days);
        Assert.Equal(1, m.NightWakings);
        Assert.Equal(TimeSpan.FromHours(6.5), m.LongestStretch);
        Assert.Equal(new TimeOnly(19, 30), m.Bedtime);
        Assert.Equal(new TimeOnly(6, 45), m.WakeUp);
        Assert.Equal(2, m.Naps);
        Assert.Equal(TimeSpan.FromMinutes(60), m.NapLength);
    }

    [Fact]
    public void Measure_NothingLogged_IsEmpty()
    {
        // Act
        var m = SleepPlanner.Measure([], At(Day1, 0), At(Day1.AddDays(7), 0), Utc);

        // Assert
        Assert.Equal(0, m.Days);
        Assert.Null(m.NightWakings);
    }

    private static SleepPlanDay EightMonthDay() => SleepPlanner.Plan(Intake(), Safe, [], 250, At(Day1, 12), Utc).Value;

    private static SleepMeasures Measures(
        int days = 7, double? wakings = 1, double? naps = 2, TimeSpan? napLength = null, TimeSpan? day = null, TimeSpan? night = null, TimeOnly? bedtime = null) =>
        new(days, wakings, TimeSpan.FromHours(5), night ?? TimeSpan.FromHours(11), day ?? TimeSpan.FromHours(2.5), naps,
            napLength ?? TimeSpan.FromMinutes(75), bedtime ?? new TimeOnly(19, 30), new TimeOnly(7, 0));

    [Fact]
    public void Suggest_SheMovedIntoTheNextAgeBand_IsANewSchedule()
    {
        // Act: planned at 8 months, checked in at a year.
        var suggestion = SleepPlanner.Suggest(EightMonthDay(), Intake(), Measures(), Measures(), 340);

        // Assert
        Assert.Equal(PlanAdjustmentKind.NewAgeSchedule, suggestion.Kind);
    }

    [Fact]
    public void Suggest_FewerThanThreeDaysLogged_KeepsGoing()
    {
        // Act
        var suggestion = SleepPlanner.Suggest(EightMonthDay(), Intake(), Measures(), Measures(days: 2, napLength: TimeSpan.FromMinutes(25)), 250);

        // Assert
        Assert.Equal(PlanAdjustmentKind.KeepGoing, suggestion.Kind);
    }

    [Fact]
    public void Suggest_ShortNaps_LengthensWindowsByFifteenMinutes()
    {
        // Act
        var suggestion = SleepPlanner.Suggest(EightMonthDay(), Intake(), Measures(), Measures(napLength: TimeSpan.FromMinutes(30)), 250);

        // Assert
        Assert.Equal(new PlanAdjustment(PlanAdjustmentKind.LongerWindows, 15), suggestion);
    }

    [Fact]
    public void Suggest_WakingsNoBetterAndTooMuchSleepInADay_ShortensTheLastNap()
    {
        // Act: 13 h of night and 3.5 h of naps is more than her age needs in 24 hours.
        var suggestion = SleepPlanner.Suggest(EightMonthDay(), Intake(), Measures(wakings: 1),
            Measures(wakings: 2, night: TimeSpan.FromHours(13), day: TimeSpan.FromHours(3.5)), 250);

        // Assert
        Assert.Equal(new PlanAdjustment(PlanAdjustmentKind.ShorterLastNap, 30), suggestion);
    }

    [Fact]
    public void Suggest_EarlierBedtimeAskedAndStillLate_MovesBedtimeFifteenMinutes()
    {
        // Arrange
        var plan = EightMonthDay();

        // Act
        var suggestion = SleepPlanner.Suggest(plan, Intake(SettlingApproach.Fading, SleepPlanGoal.EarlierBedtime), Measures(),
            Measures(bedtime: plan.Bedtime.AddMinutes(40)), 250);

        // Assert
        Assert.Equal(new PlanAdjustment(PlanAdjustmentKind.EarlierBedtime, 15), suggestion);
    }

    [Fact]
    public void Suggest_DroppingANapOnMostDays_DropsItWhereHerAgeAllows()
    {
        // Arrange: a year old on two naps who mostly takes one.
        var plan = SleepPlanner.Plan(Intake(), Safe, [], 340, At(Day1, 12), Utc).Value;

        // Act
        var suggestion = SleepPlanner.Suggest(plan, Intake(), Measures(), Measures(naps: 1.2), 340);

        // Assert
        Assert.Equal(PlanAdjustmentKind.DropANap, suggestion.Kind);
    }

    [Fact]
    public void CheckIn_AWeekOfPlan_ComparesTheWeekBeforeWithTheDaysSince()
    {
        // Arrange: a week with a night waking, then a week of plan without.
        var sleeps = Days(7, TimeSpan.FromMinutes(60), waking: true).Concat(Days(7, TimeSpan.FromMinutes(75), startDay: 7)).ToList();
        var plan = EightMonthDay();
        var started = At(Day1.AddDays(7), 7);

        // Act
        var checkIn = SleepPlanner.CheckIn(plan, Intake(), sleeps, 250, started, At(Day1.AddDays(14), 8), Utc);

        // Assert
        Assert.Equal(1, checkIn.Before.NightWakings);
        Assert.Equal(0, checkIn.Now.NightWakings);
        Assert.Equal(PlanAdjustmentKind.KeepGoing, checkIn.Suggestion.Kind);
    }
}
