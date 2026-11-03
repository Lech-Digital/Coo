using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class CareTrendsTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    // Thursday 22 October 2026, 14:00: the week of 19–25 October is under way, 12–18 October is over.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, Cest);
    private static readonly DateOnly Today = new(2026, 10, 22);
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_LastWeekTwelveHoursThisWeekThirteen_ComparesWeekByWeek()
    {
        // Arrange: the owner's example — last week she slept 12 h a day, this week 13 h.
        var entries = new List<CareEntry>();
        for (var d = 12; d <= 18; d++)
            entries.Add(Sleep(At(d, 8), At(d, 20)));
        for (var d = 19; d <= 21; d++)
            entries.Add(Sleep(At(d, 7), At(d, 20)));
        entries.Add(Sleep(At(22, 9), At(22, 11)));
        var trends = Trends(entries);

        // Act
        var buckets = await trends.ExecuteAsync(Household, Baby, TrendGrain.Week, Today, 2, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None);

        // Assert
        buckets.Should().HaveCount(2);
        buckets[0].Start.Should().Be(new DateOnly(2026, 10, 12));
        buckets[0].Sleep.Should().Be(TimeSpan.FromHours(12));
        buckets[0].DaysCounted.Should().Be(7);
        buckets[1].Sleep.Should().Be(TimeSpan.FromHours(13));
        buckets[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CurrentWeek_AveragesOnlyOverTheDaysThatAreOver()
    {
        // Arrange: Monday to Wednesday logged, and a little of today. Dividing by seven made this week look half.
        var entries = new List<CareEntry>();
        for (var d = 19; d <= 21; d++)
            entries.Add(Diaper(At(d, 9), DiaperContents.Wet));
        entries.Add(Diaper(At(22, 9), DiaperContents.Wet));
        var trends = Trends(entries);

        // Act
        var week = (await trends.ExecuteAsync(Household, Baby, TrendGrain.Week, Today, 1, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None))[0];

        // Assert
        week.DaysCounted.Should().Be(3);
        week.Diapers.Should().Be(1);
        week.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NightAcrossMidnight_CountsEachPartOnItsOwnDay()
    {
        // Arrange: 21:00 to 06:00 — three hours on the 19th, six on the 20th, never nine twice.
        var trends = Trends([Sleep(At(19, 21), At(20, 6))]);

        // Act
        var days = await trends.ExecuteAsync(Household, Baby, TrendGrain.Day, new DateOnly(2026, 10, 20), 2, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None);

        // Assert
        days[0].Sleep.Should().Be(TimeSpan.FromHours(3));
        days[1].Sleep.Should().Be(TimeSpan.FromHours(6));
        days[0].Sleeps.Should().Be(1);
        days[1].Sleeps.Should().Be(0);
        days[0].LongestSleep.Should().Be(TimeSpan.FromHours(9));
    }

    [Fact]
    public async Task ExecuteAsync_DayNobodyLogged_IsLeftOutOfTheAverage()
    {
        // Arrange: six of last week's days have 10 feeds; Wednesday was forgotten.
        var entries = new List<CareEntry>();
        foreach (var d in new[] { 12, 13, 15, 16, 17, 18 })
        {
            for (var h = 0; h < 10; h++)
                entries.Add(Bottle(At(d, 2 + (2 * h)), 90));
        }

        var trends = Trends(entries);

        // Act
        var week = (await trends.ExecuteAsync(Household, Baby, TrendGrain.Week, new DateOnly(2026, 10, 15), 1, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None))[0];

        // Assert
        week.DaysCounted.Should().Be(6);
        week.Feeds.Should().Be(10);
        week.BottleMl.Should().Be(900);
    }

    [Fact]
    public async Task ExecuteAsync_WeekWhoseOnlyLoggedDayIsToday_CountsTodayAsPartial()
    {
        // Arrange
        var trends = Trends([Diaper(At(22, 9), DiaperContents.Dirty)]);

        // Act
        var week = (await trends.ExecuteAsync(Household, Baby, TrendGrain.Week, Today, 1, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None))[0];

        // Assert
        week.DaysCounted.Should().Be(1);
        week.IsPartial.Should().BeTrue();
        week.Dirty.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_MixedDiaper_CountsAsWetAndDirty()
    {
        // Arrange
        var trends = Trends([Diaper(At(21, 9), DiaperContents.Mixed), Diaper(At(21, 12), DiaperContents.Wet)]);

        // Act
        var day = (await trends.ExecuteAsync(Household, Baby, TrendGrain.Day, new DateOnly(2026, 10, 21), 1, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None))[0];

        // Assert
        day.Diapers.Should().Be(2);
        day.Wet.Should().Be(2);
        day.Dirty.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_FutureDays_AreNeverCounted()
    {
        // Arrange
        var trends = Trends([]);

        // Act
        var days = await trends.ExecuteAsync(Household, Baby, TrendGrain.Day, Today.AddDays(3), 5, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None);

        // Assert
        days.Should().OnlyContain(d => !d.HasData);
        days.Count(d => d.IsCurrent).Should().Be(1);
    }

    [Fact]
    public async Task GetToday_NightAcrossMidnight_CountsOnlyTodaysPartLikeTheLog()
    {
        // Arrange: Today said 7 h 58 m and the Log 8 h 14 m on the owner's Pixel; both now cut at midnight.
        var store = Substitute.For<ICareLogStore>();
        var entries = new[] { Sleep(At(21, 21), At(22, 6)), Sleep(At(22, 9), At(22, 11)) };
        store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());

        // Act
        var today = await new GetToday(store).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        today.SleepSoFar.Should().Be(TimeSpan.FromHours(8));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 19)]
    [InlineData(DayOfWeek.Sunday, 18)]
    public void Range_Week_StartsOnTheGivenFirstDay(DayOfWeek first, int startDay)
    {
        // Act
        var (start, end) = GetCareTrends.Range(Today, TrendGrain.Week, first);

        // Assert
        start.Should().Be(new DateOnly(2026, 10, startDay));
        end.Should().Be(start.AddDays(6));
    }

    [Fact]
    public void Range_Month_RunsFromTheFirstToTheLastDay()
    {
        // Act
        var (start, end) = GetCareTrends.Range(new DateOnly(2026, 2, 14), TrendGrain.Month, DayOfWeek.Monday);

        // Assert
        start.Should().Be(new DateOnly(2026, 2, 1));
        end.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public async Task GetDailyAverages_BottlesOnEarlierDays_GivesMilkPerDayAndPerBottleWithoutToday()
    {
        // Arrange: the doctor's question. Two days of 6 x 100 ml and 4 x 150 ml; today's bottle is not over yet.
        var entries = new List<CareEntry>();
        for (var h = 0; h < 6; h++)
            entries.Add(Bottle(At(20, 6 + (2 * h)), 100));
        for (var h = 0; h < 4; h++)
            entries.Add(Bottle(At(21, 6 + (3 * h)), 150));
        entries.Add(Bottle(At(22, 9), 500));
        var averages = new GetDailyAverages(Trends(entries));

        // Act
        var result = await averages.ExecuteAsync(Household, Baby, 7, Amsterdam, Now, CancellationToken.None);

        // Assert
        result.DaysCounted.Should().Be(2);
        result.IncludesToday.Should().BeFalse();
        result.BottleMl.Should().Be(600m);
        result.Bottles.Should().Be(5);
        result.MlPerBottle.Should().Be(120m);
    }

    [Fact]
    public async Task GetDailyAverages_OnlyTodayLogged_CountsTodaySoFar()
    {
        // Arrange: a first day with Coo.
        var averages = new GetDailyAverages(Trends([Bottle(At(22, 9), 90), Diaper(At(22, 10), DiaperContents.Dirty)]));

        // Act
        var result = await averages.ExecuteAsync(Household, Baby, 7, Amsterdam, Now, CancellationToken.None);

        // Assert
        result.IncludesToday.Should().BeTrue();
        result.DaysCounted.Should().Be(1);
        result.Dirty.Should().Be(1);
        result.Wet.Should().Be(0);
    }

    [Fact]
    public async Task GetDailyAverages_NothingLogged_HasNoData()
    {
        // Arrange
        var averages = new GetDailyAverages(Trends([]));

        // Act
        var result = await averages.ExecuteAsync(Household, Baby, 14, Amsterdam, Now, CancellationToken.None);

        // Assert
        result.HasData.Should().BeFalse();
        result.MlPerBottle.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 45)]
    [InlineData(20, 90)]
    [InlineData(400, 180)]
    public void TypicalMillilitres_ByAge_StartsSmallForANewborn(int ageDays, int expected)
    {
        // Act
        var ml = BottleSizes.TypicalMillilitres(ageDays);

        // Assert
        ml.Should().Be(expected);
    }

    [Fact]
    public void TypicalOunces_Newborn_IsHalfAnOunce()
    {
        // Act
        var oz = BottleSizes.TypicalOunces(0);

        // Assert
        oz.Should().Be(0.5m);
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, Cest);

    private static GetCareTrends Trends(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => entries
                .Where(e => e.Timing.OccurredAt < call.ArgAt<DateTimeOffset>(2))
                .Select(e => (StoredEntry)new LiveEntry(e))
                .ToList());
        return new GetCareTrends(store);
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) =>
        Make(new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Bottle(DateTimeOffset at, int ml) =>
        Make(BottleFeed.Create([new BottleComponent(Milk.Formula, Volume.FromMillilitres(ml).Value)]).Value, EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))),
            Household,
            EntrySubject.Baby(Baby),
            details,
            timing,
            PersonRef.ThisDevicesPerson,
            EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device),
            Now).Value;
}
