using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class DayChartTests : IDisposable
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 23, 0, 0, Cest);
    private static readonly DateOnly Date = new(2026, 10, 1);
    private long _wall = 1;

    public DayChartTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task Build_NoEntries_ReturnsNoItems()
    {
        // Arrange
        var day = await Day();

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_TwoSleeps_AddsTheAwakeStretchBetweenThem()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 0)), Sleep(At(11, 40), At(12, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var awake = items.Should().ContainSingle(i => i.Shape == ChartShape.Awake).Which;
        awake.From.Should().BeApproximately(10.0 / 24, 1e-9);
        awake.To.Should().BeApproximately((11 + 40 / 60.0) / 24, 1e-9);
        awake.Label.Should().Be("awake 1 h 40 m");
        awake.EntryId.Should().BeNull();
    }

    [Fact]
    public async Task Build_OneSleep_AddsNoAwakeBeforeOrAfterIt()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().ContainSingle().Which.Shape.Should().Be(ChartShape.Sleep);
    }

    [Fact]
    public async Task Build_OverlappingSleeps_AddsNoAwakeBetweenThem()
    {
        // Arrange: a duplicate kept with "Keep both".
        var day = await Day(Sleep(At(9, 0), At(10, 0)), Sleep(At(9, 30), At(10, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().NotContain(i => i.Shape == ChartShape.Awake);
    }

    [Fact]
    public async Task Build_Sleep_LabelsItsLength()
    {
        // Arrange
        var day = await Day(Sleep(At(15, 0), At(16, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("1 h 30 m");
    }

    [Fact]
    public async Task Build_SleepAcrossMidnight_IsCutAndLabelledWithTheDaysPart()
    {
        // Arrange: 22:00 yesterday to 02:00 today.
        var day = await Day(Sleep(At(22, 0).AddDays(-1), At(2, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var sleep = items.Single();
        sleep.From.Should().Be(0);
        sleep.Label.Should().Be("2 h 0 m");
    }

    [Fact]
    public async Task Build_BreastFeed_LabelsTimeSideAndLength()
    {
        // Arrange
        var feed = Breast(At(14, 10), (BreastSide.Right, 30));
        var day = await Day(feed);

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var item = items.Single();
        item.Shape.Should().Be(ChartShape.Feed);
        item.EntryId.Should().Be(feed.Id);
        item.Label.Should().Be("14:10 R 30 m");
    }

    [Fact]
    public async Task Build_BreastFeedBothSides_LabelsEachSide()
    {
        // Arrange
        var day = await Day(Breast(At(14, 10), (BreastSide.Left, 10), (BreastSide.Right, 5)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("14:10 L 10 m · R 5 m");
    }

    [Fact]
    public async Task Build_BreastFeedInPolish_UsesPolishSideAndUnits()
    {
        // Arrange
        Strings.Use(new LanguagePreference("pl"), null);
        var day = await Day(Breast(At(14, 10), (BreastSide.Right, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("14:10 P 30 min");
    }

    [Fact]
    public async Task Build_BottleInAnOuncesHousehold_LabelsOunces()
    {
        // Arrange
        var day = await Day(Bottle(At(17, 20), 120));

        // Act
        var items = DayChart.Build(day, new Formats(true, VolumeUnit.Ounces), Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("17:20 4.1 oz");
    }

    [Fact]
    public async Task Build_BottleWithTwelveHourClock_LabelsMillilitres()
    {
        // Arrange
        var day = await Day(Bottle(At(17, 20), 120));

        // Act
        var items = DayChart.Build(day, new Formats(false, VolumeUnit.Millilitres), Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("5:20 PM 120 ml");
    }

    [Theory]
    [InlineData(DiaperContents.Wet, "Wet")]
    [InlineData(DiaperContents.Dirty, "Dirty")]
    [InlineData(DiaperContents.Mixed, "Mixed")]
    public async Task Build_Diaper_LabelsItsContents(DiaperContents contents, string expected)
    {
        // Arrange
        var day = await Day(Diaper(At(14, 45), contents));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Should().Match<ChartItem>(i => i.Shape == ChartShape.Diaper && i.Label == expected);
    }

    [Fact]
    public async Task Build_PumpWithYield_LabelsTimeAndVolume()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), 90));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("21:00 90 ml");
    }

    [Fact]
    public async Task Build_PumpWithoutYield_LabelsTheTimeOnly()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), null));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("21:00");
    }

    [Fact]
    public async Task Build_HiddenKind_LeavesTheItems()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), 90), Diaper(At(14, 45), DiaperContents.Wet));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, kind => kind != "pump");

        // Assert
        items.Should().ContainSingle().Which.Shape.Should().Be(ChartShape.Diaper);
    }

    [Fact]
    public void Time_InTheHouseholdZone_IgnoresTheMachinesZone()
    {
        // Arrange: 12:10 UTC is 14:10 in Amsterdam, whatever zone the test machine is in.
        var instant = new DateTimeOffset(2026, 10, 1, 12, 10, 0, TimeSpan.Zero);

        // Act
        var text = Ml.Time(instant, Amsterdam);

        // Assert
        text.Should().Be("14:10");
    }

    [Fact]
    public void QuietBands_Overnight_AreTwoBandsAtTheDaysEnds()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(QuietHours.Default, day, Amsterdam);

        // Assert
        bands.Should().HaveCount(2);
        bands[0].Should().Be(new ChartBand(0, 7.0 / 24));
        bands[1].From.Should().BeApproximately(22.0 / 24, 1e-9);
        bands[1].To.Should().Be(1);
    }

    [Fact]
    public void QuietBands_WithinTheDay_IsOneBand()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(new QuietHours(new TimeOnly(13, 0), new TimeOnly(15, 0)), day, Amsterdam);

        // Assert
        bands.Should().ContainSingle();
        bands[0].From.Should().BeApproximately(13.0 / 24, 1e-9);
        bands[0].To.Should().BeApproximately(15.0 / 24, 1e-9);
    }

    [Fact]
    public void QuietBands_StartEqualsEnd_IsNone()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(new QuietHours(new TimeOnly(22, 0), new TimeOnly(22, 0)), day, Amsterdam);

        // Assert
        bands.Should().BeEmpty();
    }

    [Fact]
    public void Hours_OrdinaryDay_Has25TicksFrom00To24()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var hours = DayChart.Hours(day, Amsterdam, Ml);

        // Assert
        hours.Should().HaveCount(25);
        hours[0].Should().Be(new HourTick(0, 0, "00:00"));
        hours[13].Label.Should().Be("13:00");
        hours[^1].Hour.Should().Be(24);
        hours[^1].At.Should().Be(1);
    }

    [Fact]
    public void Hours_AutumnClockChange_Has26TicksWithTwoTwos()
    {
        // Arrange: on 25 October 2026 Amsterdam goes from 03:00 back to 02:00.
        var day = DayMath.Day(new DateOnly(2026, 10, 25), Amsterdam);

        // Act
        var hours = DayChart.Hours(day, Amsterdam, Ml);

        // Assert
        hours.Should().HaveCount(26);
        hours.Select(h => h.Hour).Take(5).Should().Equal(0, 1, 2, 2, 3);
    }

    [Fact]
    public void RangeTitle_Today_NamesTodayAndRoundsToFiveMinutes()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var title = DayChart.RangeTitle(day, isToday: true, (12 * 60 + 31) / 1440.0, (18 * 60 + 29) / 1440.0, Ml, Amsterdam);

        // Assert
        title.Should().Be("Today, 12:30 – 18:30");
    }

    [Fact]
    public void RangeTitle_PastDay_NamesTheDate()
    {
        // Arrange
        var day = DayMath.Day(new DateOnly(2026, 9, 30), Amsterdam);

        // Act
        var title = DayChart.RangeTitle(day, isToday: false, 3 / 24.0, 9 / 24.0, Ml, Amsterdam);

        // Assert
        title.Should().Be("Wed 30 Sep, 03:00 – 09:00");
    }

    [Theory]
    [InlineData(14 + (52 / 60.0), 15)]
    [InlineData(15 + (20 / 60.0), 15)]
    [InlineData(0.2, 0)]
    [InlineData(23.8, 24)]
    public void NearestHour_TapAnywhere_SnapsToTheNearestWholeHour(double tappedHour, int expectedHour)
    {
        // Arrange: the owner — the zoomed day always opens on whole hours.
        var day = DayMath.Day(Date, Amsterdam);
        var hours = DayChart.Hours(day, Amsterdam, Ml);

        // Act
        var at = DayChart.NearestHour(hours, tappedHour / 24);

        // Assert
        at.Should().BeApproximately(expectedHour / 24.0, 1e-9);
    }

    [Fact]
    public void NearestHour_NoHours_KeepsTheTap()
    {
        // Arrange
        IReadOnlyList<HourTick> hours = [];

        // Act
        var at = DayChart.NearestHour(hours, 0.4);

        // Assert
        at.Should().Be(0.4);
    }

    [Fact]
    public void HitTest_NoItems_ReturnsNull()
    {
        // Arrange
        IReadOnlyList<ChartItem> items = [];

        // Act
        var hit = DayChart.HitTest(items, 0.5, 0.01);

        // Assert
        hit.Should().BeNull();
    }

    [Fact]
    public void HitTest_FeedInsideSleep_PicksTheFeed()
    {
        // Arrange: a dream feed at 23:00 inside a sleep from 21:00 to 24:00.
        var sleep = new ChartItem(ChartShape.Sleep, "sleep", 21 / 24.0, 1, new EntryId(Guid.CreateVersion7(Now)), "3 h 0 m");
        var feed = new ChartItem(ChartShape.Feed, "feeding", 23 / 24.0, 23.25 / 24, new EntryId(Guid.CreateVersion7(Now)), "23:00 R 15 m");

        // Act
        var hit = DayChart.HitTest([sleep, feed], 23.1 / 24, 0.002);

        // Assert
        hit.Should().Be(feed);
    }

    [Fact]
    public void HitTest_BottleInsideSleep_PicksTheBottle()
    {
        // Arrange: reviewer's finding — a bottle is a point; the finger lands a few dp from it, inside the sleep.
        var sleep = new ChartItem(ChartShape.Sleep, "sleep", 21 / 24.0, 1, new EntryId(Guid.CreateVersion7(Now)), "3 h 0 m");
        var bottle = new ChartItem(ChartShape.Feed, "feeding", 23 / 24.0, 23 / 24.0, new EntryId(Guid.CreateVersion7(Now)), "23:00 120 ml");

        // Act
        var hit = DayChart.HitTest([sleep, bottle], (23 / 24.0) + 0.001, 0.003);

        // Assert
        hit.Should().Be(bottle);
    }

    [Fact]
    public void HitTest_PumpDuringANap_PicksThePump()
    {
        // Arrange
        var nap = new ChartItem(ChartShape.Sleep, "sleep", 13 / 24.0, 15 / 24.0, new EntryId(Guid.CreateVersion7(Now)), "2 h 0 m");
        var pump = new ChartItem(ChartShape.Pump, "pump", 14 / 24.0, 14 / 24.0, new EntryId(Guid.CreateVersion7(Now)), "14:00");

        // Act
        var hit = DayChart.HitTest([nap, pump], (14 / 24.0) - 0.002, 0.003);

        // Assert
        hit.Should().Be(pump);
    }

    [Fact]
    public void HitTest_InsideSleepWithNothingNear_PicksTheSleep()
    {
        // Arrange
        var nap = new ChartItem(ChartShape.Sleep, "sleep", 13 / 24.0, 15 / 24.0, new EntryId(Guid.CreateVersion7(Now)), "2 h 0 m");
        var pump = new ChartItem(ChartShape.Pump, "pump", 14 / 24.0, 14 / 24.0, new EntryId(Guid.CreateVersion7(Now)), "14:00");

        // Act
        var hit = DayChart.HitTest([nap, pump], 13.2 / 24, 0.003);

        // Assert
        hit.Should().Be(nap);
    }

    [Fact]
    public void HitTest_NearAPoint_FindsItWithinTheTolerance()
    {
        // Arrange
        var diaper = new ChartItem(ChartShape.Diaper, "diaper", 0.5, 0.5, new EntryId(Guid.CreateVersion7(Now)), "Wet");

        // Act
        var near = DayChart.HitTest([diaper], 0.505, 0.008);
        var far = DayChart.HitTest([diaper], 0.52, 0.008);

        // Assert
        near.Should().Be(diaper);
        far.Should().BeNull();
    }

    [Fact]
    public void HitTest_AwakeStretch_IsNeverHit()
    {
        // Arrange
        var awake = new ChartItem(ChartShape.Awake, "sleep", 0.4, 0.5, null, "awake 2 h 24 m");

        // Act
        var hit = DayChart.HitTest([awake], 0.45, 0.01);

        // Assert
        hit.Should().BeNull();
    }

    [Fact]
    public async Task Summary_VisibleKinds_ListsSleepAndCounts()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 30)), Diaper(At(11, 0), DiaperContents.Wet), Pump(At(21, 0), At(21, 20), 90));
        var items = DayChart.Build(day, Ml, Amsterdam, kind => kind != "pump");

        // Act
        var summary = DayChart.Summary(items, day.Day, kind => kind != "pump");

        // Assert
        summary.Should().Be("Sleep 1 h 30 m; Feeds 0; Diapers 1");
    }

    [Fact]
    public async Task Build_AFeedInThreeParts_DrawsEachPartAsItsOwnBarWithTheGapsEmpty()
    {
        // Arrange: left 14:00–14:10, a 3 minute pause, right 14:13–14:18, a 2 minute pause, left 14:20–14:24 (Spec 011d §5).
        var feed = InParts((BreastSide.Left, At(14, 0), At(14, 10)), (BreastSide.Right, At(14, 13), At(14, 18)), (BreastSide.Left, At(14, 20), At(14, 24)));
        var day = await Day(feed);

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().HaveCount(3).And.OnlyContain(i => i.Shape == ChartShape.Feed && i.EntryId == feed.Id);
        items.Select(i => (i.From, i.To)).Should().Equal(
            [(At(14, 0), At(14, 10)), (At(14, 13), At(14, 18)), (At(14, 20), At(14, 24))],
            (bar, part) => Math.Abs(bar.From - day.Day.Fraction(part.Item1)) < 1e-9 && Math.Abs(bar.To - day.Day.Fraction(part.Item2)) < 1e-9);
        items[0].Label.Should().Be("14:00 L 14 m · R 5 m · in 3 parts");
        items.Skip(1).Should().OnlyContain(i => i.Label.Length == 0, "the feed is labelled once, at its first part");
    }

    [Fact]
    public async Task Build_AFeedWithAPartAfterMidnight_ClampsThatPartToTheDaysEnd()
    {
        // Arrange: left 23:40–23:50, then right 00:05–00:15 the next day. Feeds are charted on their start day only, and a
        // bar is cut at the day's end, as a feed across midnight always was: the late part stays, clamped to the edge.
        var segments = new[]
        {
            new BreastSegment(BreastSide.Left, At(23, 40), At(23, 50)),
            new BreastSegment(BreastSide.Right, At(0, 5).AddDays(1), At(0, 15).AddDays(1)),
        };
        var feed = CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby),
            BreastFeed.Create(segments, isCompleted: true).Value, null, PersonRef.ThisDevicesPerson, EntrySource.Timer,
            new HlcStamp(++_wall, 0, Device), At(0, 20).AddDays(1)).Value;
        var day = await Day(feed);

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().HaveCount(2).And.OnlyContain(i => i.Shape == ChartShape.Feed && i.EntryId == feed.Id);
        items[0].From.Should().BeApproximately(day.Day.Fraction(At(23, 40)), 1e-9);
        items[0].To.Should().BeApproximately(day.Day.Fraction(At(23, 50)), 1e-9);
        items[0].Label.Should().StartWith("23:40");
        (items[1].From, items[1].To).Should().Be((1.0, 1.0));
        items[1].Label.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_ASideSwitchWithoutAPause_IsOneBar()
    {
        // Arrange
        var day = await Day(Breast(At(14, 10), (BreastSide.Left, 10), (BreastSide.Right, 5)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().ContainSingle().Which.To.Should().BeApproximately(day.Day.Fraction(At(14, 25)), 1e-9);
    }

    [Fact]
    public async Task Summary_AFeedInParts_CountsAsOneFeed()
    {
        // Arrange
        var day = await Day(InParts((BreastSide.Left, At(14, 0), At(14, 10)), (BreastSide.Right, At(14, 13), At(14, 18))));
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Act
        var summary = DayChart.Summary(items, day.Day, kind => kind == "feeding");

        // Assert
        summary.Should().Be("Feeds 1");
    }

    [Fact]
    public async Task HitTest_TheSecondPartOfAFeed_OpensThatFeed()
    {
        // Arrange
        var feed = InParts((BreastSide.Left, At(14, 0), At(14, 10)), (BreastSide.Right, At(14, 30), At(14, 40)));
        var items = DayChart.Build(await Day(feed), Ml, Amsterdam, _ => true);

        // Act
        var hit = DayChart.HitTest(items, (14 + 35 / 60.0) / 24, 0.001);

        // Assert
        hit!.EntryId.Should().Be(feed.Id);
    }

    private static Formats Ml => new(true, VolumeUnit.Millilitres);

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, Cest);

    private static async Task<DaySummary> Day(params CareEntry[] entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default)
            .ReturnsForAnyArgs([.. entries.Select(e => (StoredEntry)new LiveEntry(e))]);
        return await new GetDay(store).ExecuteAsync(Household, Baby, Date, Amsterdam, Now, CancellationToken.None);
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(EntrySubject.Baby(Baby), new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Breast(DateTimeOffset start, params (BreastSide Side, int Minutes)[] sides) =>
        Make(EntrySubject.Baby(Baby), BreastFeed.FromSideDurations(start, sides).Value, null);

    private CareEntry InParts(params (BreastSide Side, DateTimeOffset Start, DateTimeOffset End)[] segments) =>
        Make(EntrySubject.Baby(Baby), BreastFeed.Create(segments.Select(s => new BreastSegment(s.Side, s.Start, s.End)), isCompleted: true).Value, null);

    private CareEntry Bottle(DateTimeOffset at, int ml) =>
        Make(EntrySubject.Baby(Baby), BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(ml).Value).Value, EntryTiming.Point(at));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) =>
        Make(EntrySubject.Baby(Baby), new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Pump(DateTimeOffset start, DateTimeOffset end, int? ml) =>
        Make(EntrySubject.Caregiver(PersonRef.ThisDevicesPerson),
            new PumpDetails(ml is { } v ? PumpYield.OfTotal(Volume.FromMillilitres(v).Value) : null),
            EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Make(EntrySubject subject, EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, subject, details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;
}
