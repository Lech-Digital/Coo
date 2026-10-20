using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class CareStatisticsTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, Cest);
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_WeekPeriod_CalculatesAveragesCorrectly()
    {
        // 7 days from Monday 12 Oct to Sunday 18 Oct 2026
        var anchor = new DateOnly(2026, 10, 15);
        var entries = new List<CareEntry>();

        // 2 sleeps per day of 2 hours each = 4 hours/day, 28 hours total
        for (var d = 12; d <= 18; d++)
        {
            var date = new DateTimeOffset(2026, 10, d, 10, 0, 0, Cest);
            entries.Add(Sleep(date, date.AddHours(2)));
            entries.Add(Sleep(date.AddHours(4), date.AddHours(6)));
            entries.Add(Breast(date.AddHours(1), 10, 15)); // 25 min breastfeed
            entries.Add(Diaper(date.AddHours(2), DiaperContents.Wet));
            entries.Add(Diaper(date.AddHours(5), DiaperContents.Dirty));
        }

        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => new LiveEntry(e)).ToList());

        var service = new GetCareStatistics(store);

        // Act
        var stats = await service.ExecuteAsync(Household, Baby, anchor, CarePeriod.Week, Amsterdam, Now, CancellationToken.None);

        // Assert
        stats.TotalDays.Should().Be(7);
        stats.Sleep.TotalSessions.Should().Be(14);
        stats.Sleep.SessionsPerDay.Should().Be(2.0);
        stats.Sleep.TotalSleep.Should().Be(TimeSpan.FromHours(28));
        stats.Sleep.DailyAverage.Should().Be(TimeSpan.FromHours(4));
        stats.Sleep.AverageDuration.Should().Be(TimeSpan.FromHours(2));

        stats.Feeding.TotalFeeds.Should().Be(7);
        stats.Feeding.FeedsPerDay.Should().Be(1.0);
        stats.Feeding.AverageBreastDuration.Should().Be(TimeSpan.FromMinutes(25));
        stats.Feeding.AverageLeftDuration.Should().Be(TimeSpan.FromMinutes(10));
        stats.Feeding.AverageRightDuration.Should().Be(TimeSpan.FromMinutes(15));

        stats.Diapers.TotalDiapers.Should().Be(14);
        stats.Diapers.DiapersPerDay.Should().Be(2.0);
        stats.Diapers.WetCount.Should().Be(7);
        stats.Diapers.DirtyCount.Should().Be(7);
    }

    [Fact]
    public void PeriodRange_Week_StartsOnMondayAndEndsOnSunday()
    {
        // Thursday 15 Oct 2026 -> Mon 12 Oct to Sun 18 Oct
        var (start, end) = GetCareStatistics.PeriodRange(new DateOnly(2026, 10, 15), CarePeriod.Week);
        start.Should().Be(new DateOnly(2026, 10, 12));
        end.Should().Be(new DateOnly(2026, 10, 18));
    }

    [Fact]
    public void PeriodRange_Month_StartsOnFirstAndEndsOnLastDay()
    {
        var (start, end) = GetCareStatistics.PeriodRange(new DateOnly(2026, 10, 15), CarePeriod.Month);
        start.Should().Be(new DateOnly(2026, 10, 1));
        end.Should().Be(new DateOnly(2026, 10, 31));
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) =>
        Make(new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Breast(DateTimeOffset start, int leftMinutes, int rightMinutes) =>
        Make(BreastFeed.FromSideDurations(start, [(BreastSide.Left, leftMinutes), (BreastSide.Right, rightMinutes)]).Value, null);

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
