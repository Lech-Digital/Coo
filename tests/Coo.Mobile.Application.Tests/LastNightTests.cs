using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Insights;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>The morning note: last night measured from the log, shown until noon.</summary>
public sealed class LastNightTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Laura = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly BabyProfile Baby =
        BabyProfile.Create(Laura, Household, "Laura", [], new DateOnly(2026, 5, 2), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 11, 4)).Value;
    private long _wall = 1;

    [Fact]
    public async Task Execute_AMorningAfterANightWithOneWaking_MeasuresItAndCountsTheNightFeed()
    {
        // Arrange: a nap yesterday afternoon, bed at 19:30, awake 02:00–02:20 with a feed, up at 06:30.
        var entries = new List<CareEntry>
        {
            Sleep(At(3, 13, 0), At(3, 14, 0)),
            Sleep(At(3, 19, 30), At(4, 2, 0)),
            Feed(At(4, 2, 5)),
            Sleep(At(4, 2, 20), At(4, 6, 30)),
        };

        // Act
        var morning = await Use(entries).ExecuteAsync(Household, Baby, null, Amsterdam, At(4, 7, 30), CancellationToken.None);
        var afternoon = await Use(entries).ExecuteAsync(Household, Baby, null, Amsterdam, At(4, 13, 0), CancellationToken.None);

        // Assert
        morning.Should().NotBeNull();
        morning!.Night.NightSleep.Should().Be(TimeSpan.FromMinutes(6 * 60 + 30 + 4 * 60 + 10));
        morning.Night.NightWakings.Should().Be(1);
        morning.Night.LongestStretch.Should().Be(TimeSpan.FromMinutes(6 * 60 + 30));
        morning.Feeds.Should().Be(1);
        morning.BeforePlan.Should().BeNull();
        afternoon.Should().BeNull("after noon the night is no longer news");
    }

    [Fact]
    public async Task Execute_StillAsleepOrNothingLogged_HasNoNote()
    {
        // Arrange
        var asleep = new List<CareEntry> { Sleep(At(3, 19, 30), null) };

        // Act
        var running = await Use(asleep).ExecuteAsync(Household, Baby, null, Amsterdam, At(4, 6, 0), CancellationToken.None);
        var empty = await Use([]).ExecuteAsync(Household, Baby, null, Amsterdam, At(4, 7, 0), CancellationToken.None);

        // Assert
        running.Should().BeNull();
        empty.Should().BeNull();
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(1));

    private static GetLastNight Use(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        return new GetLastNight(store);
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset? end) =>
        Make(new SleepDetails(), EntryTiming.Session(end is { } e ? TimeRange.Create(start, e).Value : TimeRange.Open(start)));

    private CareEntry Feed(DateTimeOffset at) =>
        Make(BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(90).Value).Value, EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(At(4, 8, 0).AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Laura), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), At(4, 8, 0)).Value;
}
