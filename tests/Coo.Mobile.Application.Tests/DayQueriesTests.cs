using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

public sealed class DayQueriesTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId Twin = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, Cest);
    private long _wall = 1;

    [Fact]
    public void Day_AutumnClockChange_Is25HoursLong()
    {
        // Arrange
        var date = new DateOnly(2026, 10, 25);

        // Act
        var day = DayMath.Day(date, Amsterdam);

        // Assert
        day.Length.Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void Day_SpringClockChange_Is23HoursLong()
    {
        // Arrange
        var date = new DateOnly(2026, 3, 29);

        // Act
        var day = DayMath.Day(date, Amsterdam);

        // Assert
        day.Length.Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task ExecuteAsync_LastSleepEnded_AwakeSinceIsItsEnd()
    {
        // Arrange
        var store = new FakeStore(Sleep(At(9, 0), At(10, 15)), Sleep(At(5, 0), At(6, 0)));

        // Act
        var today = await new GetToday(store).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        today.AwakeSince.Should().Be(At(10, 15));
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunning_ListsItAsRunningWithNoAwakeSince()
    {
        // Arrange
        var store = new FakeStore(Sleep(At(13, 10), null));

        // Act
        var today = await new GetToday(store).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        today.Running.Should().ContainSingle();
        today.AwakeSince.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_LastFeedYesterday_StillFoundButNotInTodaysTimeline()
    {
        // Arrange
        var store = new FakeStore(Diaper(At(23, 50).AddDays(-1)));

        // Act
        var today = await new GetToday(store).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        today.LastDiaper.Should().NotBeNull();
        today.TodayEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TwinsEntry_IsNotThisBabys()
    {
        // Arrange
        var store = new FakeStore(Diaper(At(11, 0), Twin));

        // Act
        var today = await new GetToday(store).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        today.LastDiaper.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SleepAcrossMidnight_IsSplitBetweenBothDays()
    {
        // Arrange: 20:00 yesterday to 06:30 today.
        var store = new FakeStore(Sleep(At(20, 0).AddDays(-1), At(6, 30)));
        var getDay = new GetDay(store);

        // Act
        var yesterday = await getDay.ExecuteAsync(Household, Baby, new DateOnly(2026, 9, 30), Amsterdam, Now, CancellationToken.None);
        var today = await getDay.ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        yesterday.Totals.Sleep.Should().Be(TimeSpan.FromHours(4));
        today.Totals.Sleep.Should().Be(TimeSpan.FromHours(6.5));
        today.Ribbon.Should().ContainSingle(m => m.IsBlock).Which.From.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_DayTotals_CountFeedsAndDiapersStartingThatDay()
    {
        // Arrange
        var store = new FakeStore(Bottle(At(8, 0)), Bottle(At(11, 0)), Diaper(At(9, 0)), Diaper(At(23, 0).AddDays(-1)));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Totals.Feeds.Should().Be(2);
        day.Totals.Diapers.Should().Be(1);
        day.Ribbon.Where(m => !m.IsBlock).Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_BreastFeed_MarkRunsFromStartToEndWithItsEntryId()
    {
        // Arrange
        var feed = Breast(At(10, 0), 30);
        var store = new FakeStore(feed);

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        var mark = day.Ribbon.Should().ContainSingle().Which;
        mark.EntryId.Should().Be(feed.Id);
        mark.From.Should().BeApproximately(10.0 / 24, 1e-9);
        mark.To.Should().BeApproximately(10.5 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_Bottle_MarkIsAPoint()
    {
        // Arrange
        var store = new FakeStore(Bottle(At(8, 0)));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        var mark = day.Ribbon.Should().ContainSingle().Which;
        mark.To.Should().Be(mark.From);
    }

    [Fact]
    public async Task ExecuteAsync_PumpSession_MarkRunsToItsEnd()
    {
        // Arrange
        var store = new FakeStore(Pump(At(9, 0), At(9, 30)));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().BeApproximately(9.5 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunningToday_MarkEndsAtNow()
    {
        // Arrange: now is 14:00.
        var store = new FakeStore(Sleep(At(13, 0), null));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().BeApproximately(14.0 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunningSinceYesterday_YesterdaysMarkEndsAtMidnight()
    {
        // Arrange
        var store = new FakeStore(Sleep(At(23, 0).AddDays(-1), null));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 9, 30), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().Be(1);
    }

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, Cest);

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset? end) =>
        Make(EntrySubject.Baby(Baby), new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, BabyId? baby = null) =>
        Make(EntrySubject.Baby(baby ?? Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(at));

    private CareEntry Bottle(DateTimeOffset at) =>
        Make(EntrySubject.Baby(Baby), BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(120).Value).Value, EntryTiming.Point(at));

    private CareEntry Breast(DateTimeOffset start, int rightMinutes) =>
        Make(EntrySubject.Baby(Baby), BreastFeed.FromSideDurations(start, [(BreastSide.Right, rightMinutes)]).Value, null);

    private CareEntry Pump(DateTimeOffset start, DateTimeOffset end) =>
        Make(EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Make(EntrySubject subject, EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, subject, details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;

    /// <summary>The store port, in memory: a fake of the outbound port, never of Domain objects.</summary>
    private sealed class FakeStore(params CareEntry[] entries) : ICareLogStore
    {
        public Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredEntry>>(
                [.. entries.Where(e => e.Timing.OccurredAt >= windowStart && e.Timing.OccurredAt < windowEnd)
                    .OrderByDescending(e => e.Timing.OccurredAt).Select(e => new LiveEntry(e))]);

        public Task SaveAsync(CareEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class AttentionTests
{
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, Cest);
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_TwoDiapersAMinuteApart_ReportsOnePair()
    {
        // Arrange
        var (a, b) = (Diaper(Now.AddMinutes(-10)), Diaper(Now.AddMinutes(-9)));
        var store = NSubstitute.Substitute.For<ICareLogStore>();
        store.ListAsync(Household, NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns([new LiveEntry(a), new LiveEntry(b)]);
        var dismissals = NSubstitute.Substitute.For<IDuplicateDismissalStore>();
        dismissals.ListAsync(Household, NSubstitute.Arg.Any<CancellationToken>()).Returns(new HashSet<Coo.Domain.CareLog.Duplicates.EntryPair>());

        // Act
        var attention = await new GetAttention(store, dismissals, NoRejections()).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        attention.Duplicates.Should().ContainSingle();
        attention.Count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_PairKeptBoth_IsNotReported()
    {
        // Arrange
        var (a, b) = (Diaper(Now.AddMinutes(-10)), Diaper(Now.AddMinutes(-9)));
        var store = NSubstitute.Substitute.For<ICareLogStore>();
        store.ListAsync(Household, NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns([new LiveEntry(a), new LiveEntry(b)]);
        var dismissals = NSubstitute.Substitute.For<IDuplicateDismissalStore>();
        dismissals.ListAsync(Household, NSubstitute.Arg.Any<CancellationToken>())
            .Returns(new HashSet<Coo.Domain.CareLog.Duplicates.EntryPair> { new(b.Id, a.Id) });

        // Act
        var attention = await new GetAttention(store, dismissals, NoRejections()).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        attention.Count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_QuarantinedEntry_IsReported()
    {
        // Arrange
        var store = NSubstitute.Substitute.For<ICareLogStore>();
        store.ListAsync(Household, NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<DateTimeOffset>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns([new QuarantinedEntry(new EntryId(Guid.CreateVersion7(Now)), "feeding", Now, "breast.no-segments")]);
        var dismissals = NSubstitute.Substitute.For<IDuplicateDismissalStore>();
        dismissals.ListAsync(Household, NSubstitute.Arg.Any<CancellationToken>()).Returns(new HashSet<Coo.Domain.CareLog.Duplicates.EntryPair>());

        // Act
        var attention = await new GetAttention(store, dismissals, NoRejections()).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        attention.Quarantined.Should().ContainSingle();
    }

    private CareEntry Diaper(DateTimeOffset at) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(at.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby),
            new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(at), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), Now).Value;

    private static Coo.Mobile.Application.Sync.ISyncStore NoRejections()
    {
        var sync = NSubstitute.Substitute.For<Coo.Mobile.Application.Sync.ISyncStore>();
        sync.ListRejectionsAsync(NSubstitute.Arg.Any<HouseholdId>(), NSubstitute.Arg.Any<CancellationToken>()).Returns([]);
        return sync;
    }
}
