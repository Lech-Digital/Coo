using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Notifications;
using Coo.Mobile.Application.Preferences;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class ReminderPlannerTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    // Friday 23 October 2026, 14:00 in Amsterdam.
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(2));
    private long _wall = 1;

    [Fact]
    public void Plan_FeedEveryThreeHours_RemindsThreeHoursAfterTheLastFeedStarted()
    {
        // Arrange
        var feed = Entry(Bottle(), EntryTiming.Point(Now.AddHours(-1)));
        var settings = ReminderSettings.Off with { FeedEvery = TimeSpan.FromHours(3) };

        // Act
        var planned = ReminderPlanner.Plan(Today(lastFeed: feed), null, settings, QuietHours.Default, Zone, Now);

        // Assert
        var reminder = planned.Should().ContainSingle().Subject;
        reminder.Should().Match<PlannedReminder>(r => r.Id == "feed" && r.At == Now.AddHours(2) && r.Since == Now.AddHours(-1));
    }

    [Fact]
    public void Plan_FeedDueDuringQuietHours_IsLeftOutUnlessAlsoAtNight()
    {
        // Arrange: fed at 21:00, so three hours later is midnight.
        var evening = new DateTimeOffset(2026, 10, 23, 21, 0, 0, TimeSpan.FromHours(2));
        var feed = Entry(Bottle(), EntryTiming.Point(evening));
        var settings = ReminderSettings.Off with { FeedEvery = TimeSpan.FromHours(3) };

        // Act
        var quiet = ReminderPlanner.Plan(Today(lastFeed: feed), null, settings, QuietHours.Default, Zone, evening.AddMinutes(5));
        var night = ReminderPlanner.Plan(Today(lastFeed: feed), null, settings with { FeedAtNight = true }, QuietHours.Default, Zone, evening.AddMinutes(5));

        // Assert
        quiet.Should().BeEmpty();
        night.Should().ContainSingle().Which.At.Should().Be(evening.AddHours(3));
    }

    [Fact]
    public void Plan_FeedRunning_NoFeedReminderButATimerReminder()
    {
        // Arrange: a breastfeed running for 10 minutes.
        var running = Entry(BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-10)), null);
        var settings = new ReminderSettings(TimeSpan.FromHours(3), false, true, false);

        // Act
        var planned = ReminderPlanner.Plan(Today(running: [running], lastFeed: running), null, settings, QuietHours.Default, Zone, Now);

        // Assert
        var timer = planned.Should().ContainSingle().Subject;
        timer.Kind.Should().Be(ReminderKind.Timer);
        timer.Id.Should().Be($"timer:{running.Id.Value}");
        timer.At.Should().Be(Now.AddMinutes(35));
    }

    [Fact]
    public void Plan_NapWindow_TheChosenLeadBeforeThePredictedWindowOpens()
    {
        // Arrange: awake since 13:30, the window predicted to open at 15:00, reminded ten minutes before.
        var settings = ReminderSettings.Off with { NapWindow = true, NapLeadMinutes = 10 };
        var sleep = Window(Now.AddMinutes(-30), Now.AddMinutes(60));

        // Act
        var planned = ReminderPlanner.Plan(Today(awakeSince: Now.AddMinutes(-30)), sleep, settings, QuietHours.Default, Zone, Now);

        // Assert
        planned.Should().ContainSingle().Which.Should().Match<PlannedReminder>(
            r => r.Kind == ReminderKind.Nap && r.At == Now.AddMinutes(50) && r.Opens == Now.AddMinutes(60));
    }

    [Fact]
    public void Plan_PredictedBedtime_IsABedtimeReminder()
    {
        // Arrange
        var settings = ReminderSettings.Off with { NapWindow = true };
        var sleep = Window(Now.AddMinutes(-30), Now.AddMinutes(60), bedtime: true);

        // Act
        var planned = ReminderPlanner.Plan(Today(awakeSince: Now.AddMinutes(-30)), sleep, settings, QuietHours.Default, Zone, Now);

        // Assert
        planned.Should().ContainSingle().Which.Kind.Should().Be(ReminderKind.Bedtime);
    }

    [Fact]
    public void Plan_NapWindowOnWithoutAPrediction_PlansNothing()
    {
        // Arrange: no prediction (none yet, or no Premium)
        var settings = ReminderSettings.Off with { NapWindow = true };

        // Act
        var planned = ReminderPlanner.Plan(Today(awakeSince: Now.AddMinutes(-30)), null, settings, QuietHours.Default, Zone, Now);

        // Assert
        planned.Should().BeEmpty();
    }

    private static SleepPrediction Window(DateTimeOffset awake, DateTimeOffset opens, bool bedtime = false) => new(
        1, bedtime, awake, opens, opens.AddMinutes(15), opens.AddMinutes(30), PredictionConfidence.High, opens.AddMinutes(15) - awake,
        null, 0, 0, WakeWindowPrior.For(64), false, SleepPredictor.Version);

    [Fact]
    public void Plan_ReminderTimeAlreadyPassed_PlansNothing()
    {
        // Arrange: fed four hours ago with a three-hour reminder; asleep, so no nap reminder either.
        var feed = Entry(Bottle(), EntryTiming.Point(Now.AddHours(-4)));
        var sleep = Entry(new SleepDetails(), EntryTiming.Session(TimeRange.Open(Now.AddMinutes(-20))));
        var settings = new ReminderSettings(TimeSpan.FromHours(3), true, true, true);

        // Act
        var planned = ReminderPlanner.Plan(Today(running: [sleep], lastFeed: feed, awakeSince: Now.AddHours(-2)), Window(Now.AddHours(-2), Now.AddHours(1)), settings, QuietHours.Default, Zone, Now);

        // Assert
        planned.Should().BeEmpty();
    }

    [Fact]
    public void Plan_AllOff_PlansNothing()
    {
        // Arrange
        var feed = Entry(Bottle(), EntryTiming.Point(Now.AddHours(-1)));

        // Act
        var planned = ReminderPlanner.Plan(Today(lastFeed: feed, awakeSince: Now), null, ReminderSettings.Off, QuietHours.Default, Zone, Now);

        // Assert
        planned.Should().BeEmpty();
    }

    private static BottleFeed Bottle() => BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(120).Value).Value;

    private static TodaySummary Today(IReadOnlyList<CareEntry>? running = null, CareEntry? lastFeed = null, DateTimeOffset? awakeSince = null) =>
        new(running ?? [], lastFeed, null, null, null, awakeSince, [], DayMath.Day(DayMath.DateOf(Now, Zone), Zone));

    private CareEntry Entry(EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;
}
