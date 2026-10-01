using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>Only the outbound ports are mocked; Domain objects are real (testing standards).</summary>
public sealed class CareLogUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly FixedTime _time = new(Now);
    private long _wall = 1_790_000_000_000;

    public CareLogUseCaseTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task ExecuteAsync_ValidDiaper_SavesEntryWithStampAndReturnsIt()
    {
        // Arrange
        var useCase = new LogEntry(_store, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(DiaperCommand(Now.AddMinutes(-2)), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Version.WallClockMs.Should().Be(_wall);
        await _store.Received(1).SaveAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DomainRuleFails_ReturnsErrorAndWritesNothing()
    {
        // Arrange: two days in the future.
        var useCase = new LogEntry(_store, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(DiaperCommand(Now.AddDays(2)), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.TooFarInFuture);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ReviseUnknownEntry_FailsWithNotFound()
    {
        // Arrange
        var useCase = new ReviseEntry(_store, _clock, _time);
        _store.FindAsync(Arg.Any<EntryId>(), Arg.Any<CancellationToken>()).Returns((StoredEntry?)null);

        // Act
        var result = await useCase.ExecuteAsync(new EntryId(Guid.CreateVersion7(Now)), (_, _, _) => Result.Success, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(ApplicationErrors.EntryNotFound);
    }

    [Fact]
    public async Task ExecuteAsync_ReviseReadOnlyEntry_FailsWithNotEditable()
    {
        // Arrange
        var useCase = new ReviseEntry(_store, _clock, _time);
        var id = new EntryId(Guid.CreateVersion7(Now));
        _store.FindAsync(id, Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyEntry(id, "medication", Now, null, PersonRef.ThisDevicesPerson, EntryErrors.KindUnknown));

        // Act
        var result = await useCase.ExecuteAsync(id, (_, _, _) => Result.Success, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(ApplicationErrors.EntryNotEditable);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchSideOnRunningFeed_SavesRevisedEntry()
    {
        // Arrange
        var running = CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby),
            BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-8)), null, PersonRef.ThisDevicesPerson, EntrySource.Timer,
            new HlcStamp(1, 0, Device), Now).Value;
        _store.FindAsync(running.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(running));
        var useCase = new ReviseEntry(_store, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(
            running.Id, (entry, version, now) => entry.Revise<BreastFeed>(d => d.SwitchSide(now), version, now), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        ((BreastFeed)result.Value.Details).LastSide.Should().Be(BreastSide.Right);
        await _store.Received(1).SaveAsync(running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FirstLaunchInTheUs_CreatesHouseholdInOunces()
    {
        // Arrange
        var households = Substitute.For<IHouseholdStore>();
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var locale = Substitute.For<IDeviceLocale>();
        locale.TimeZoneId.Returns("America/New_York");
        locale.PrefersOunces.Returns(true);
        var useCase = new EnsureProvisionalHousehold(households, _clock, locale, _time);

        // Act
        var settings = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        settings.VolumeUnit.Should().Be(VolumeUnit.Ounces);
        settings.TimeZoneId.Should().Be("America/New_York");
        await households.Received(1).SaveSettingsAsync(settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SecondLaunch_ReturnsExistingHouseholdWithoutWriting()
    {
        // Arrange
        var existing = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var households = Substitute.For<IHouseholdStore>();
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        var useCase = new EnsureProvisionalHousehold(households, _clock, Substitute.For<IDeviceLocale>(), _time);

        // Act
        var settings = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        settings.Should().BeSameAs(existing);
        await households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    private static LogEntryCommand DiaperCommand(DateTimeOffset at) =>
        new(Household, EntrySubject.Baby(Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(at),
            PersonRef.ThisDevicesPerson, EntrySource.Manual);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
