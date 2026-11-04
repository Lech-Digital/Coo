using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
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

public sealed class GetSleepPredictionTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId BabyId = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId Twin = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    // Thursday 22 October 2026, 07:10: the baby woke at 07:00.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 7, 10, 0, Cest);
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_AWeekOfNightsAndNaps_PredictsFromThisBabysSleeps()
    {
        // Arrange: nights 19:00–07:00 and a first nap at 09:00, for a week; the twin's naps are much later
        var entries = new List<CareEntry>();
        for (var d = 15; d <= 21; d++)
        {
            entries.Add(Sleep(BabyId, At(d, 9), At(d, 10)));
            entries.Add(Sleep(BabyId, At(d, 19), At(d + 1, 7)));
            entries.Add(Sleep(Twin, At(d, 11), At(d, 12)));
        }

        entries.Add(Make(BabyId, new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(At(21, 8))));

        // Act
        var prediction = await Use(entries).ExecuteAsync(Household, Baby(new DateOnly(2026, 4, 22)), Amsterdam, Now, CancellationToken.None);

        // Assert
        prediction.Should().NotBeNull();
        prediction!.Position.Should().Be(1);
        prediction.PersonalMedian.Should().Be(TimeSpan.FromHours(2), "the twin's sleeps are not this baby's");
        prediction.AwakeSince.Should().Be(At(22, 7));
    }

    [Fact]
    public async Task ExecuteAsync_BabyNotBornYet_ReturnsNull()
    {
        // Arrange
        var expected = BabyProfile.Create(BabyId, Household, "Lena", [], null, new DateOnly(2026, 12, 1), new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 22)).Value;

        // Act
        var prediction = await Use([]).ExecuteAsync(Household, expected, Amsterdam, Now, CancellationToken.None);

        // Assert
        prediction.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NoSleepLogged_ReturnsNull()
    {
        // Arrange
        var diaper = Make(BabyId, new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(At(21, 8)));

        // Act
        var prediction = await Use([diaper]).ExecuteAsync(Household, Baby(new DateOnly(2026, 4, 22)), Amsterdam, Now, CancellationToken.None);

        // Assert
        prediction.Should().BeNull();
    }

    [Fact]
    public async Task TrackRecordAsync_TwoWeeksOfSteadyMornings_CountsHerWindowsNotTheTwins()
    {
        // Arrange: nights 19:00–07:00 and a first nap at 09:00 since 1 October; the twin naps at 11:00.
        var entries = new List<CareEntry>();
        for (var d = 1; d <= 21; d++)
        {
            entries.Add(Sleep(BabyId, At(d, 9), At(d, 10)));
            entries.Add(Sleep(BabyId, At(d, 19), At(d + 1, 7)));
            entries.Add(Sleep(Twin, At(d, 11), At(d, 12)));
        }

        // Act
        var record = await Use(entries).TrackRecordAsync(Household, Baby(new DateOnly(2026, 4, 22)), Amsterdam, Now, CancellationToken.None);

        // Assert: the morning nap is predicted every day of the fortnight, from her own 2-hour windows.
        record.Should().NotBeNull();
        record!.IsEnough.Should().BeTrue();
        record.Hits.Should().BeGreaterThanOrEqualTo(10);
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, Cest);

    private static BabyProfile Baby(DateOnly born) =>
        BabyProfile.Create(BabyId, Household, "Lena", [], born, null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 22)).Value;

    private static GetSleepPrediction Use(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        return new GetSleepPrediction(store);
    }

    private CareEntry Sleep(BabyId baby, DateTimeOffset start, DateTimeOffset end) =>
        Make(baby, new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Make(BabyId baby, EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))),
            Household,
            EntrySubject.Baby(baby),
            details,
            timing,
            PersonRef.ThisDevicesPerson,
            EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device),
            Now).Value;
}
