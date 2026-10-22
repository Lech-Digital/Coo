using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.CareLog;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class AdvisorContextTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(2));
    private long _wall = 1;

    [Fact]
    public async Task ExecuteAsync_NapsLoggedToday_TellsTheAdvisorAboutThem()
    {
        // Arrange: the owner's finding — two naps today, and the Advisor said nothing had been logged for days.
        var context = Context([
            Sleep(At(22, 11, 45), At(22, 11, 50)),
            Sleep(At(22, 11, 53), At(22, 13, 17)),
            Diaper(At(22, 10, 52), DiaperContents.Wet),
        ]);

        // Act
        var text = await context.ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("Thu 22 Oct (today so far): sleep 1 h 29 m in 2 sleeps");
        text.Should().Contain("Awake since Thu 13:17");
        text.Should().Contain("diapers 1 (1 wet, 0 dirty)");
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunning_SaysSoInsteadOfAwake()
    {
        // Arrange
        var running = Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(At(22, 13, 30), null).Value));
        var context = Context([running]);

        // Act
        var text = await context.ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("In progress: sleep of 30 m since Thu 13:30");
        text.Should().NotContain("Awake since");
    }

    [Fact]
    public async Task ExecuteAsync_NothingLogged_SaysSoForEachDay()
    {
        // Act
        var text = await Context([]).ExecuteAsync(Household, Baby, Amsterdam, Now, CancellationToken.None);

        // Assert
        text.Should().Contain("nothing logged");
        text.Should().Contain("This week so far: nothing logged.");
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(2));

    private static BuildAdvisorContext Context(IReadOnlyList<CareEntry> entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(_ => entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());
        return new BuildAdvisorContext(new GetToday(store), new GetCareTrends(store));
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) => Make(new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming timing) =>
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
