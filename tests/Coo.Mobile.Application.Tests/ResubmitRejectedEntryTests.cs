using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>A rejected entry is written again, unchanged, with a new version (final review of Spec 011d, M1).</summary>
public sealed class ResubmitRejectedEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1_790_000_000_000;

    public ResubmitRejectedEntryTests()
    {
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task ExecuteAsync_ARejectedFeedInParts_WritesItWithANewVersionAndTheSameSegments()
    {
        // Arrange
        var feed = FeedInParts();
        var (details, version) = (feed.Details, feed.Version);
        Reject(feed);
        var useCase = new ResubmitRejectedEntry(_store, _sync, _clock);

        // Act
        var written = await useCase.ExecuteAsync(feed.Id, CancellationToken.None);

        // Assert
        written.Should().NotBeNull();
        written!.Details.Should().Be(details);
        written.Version.Should().BeGreaterThan(version);
        await _store.Received(1).SaveAsync(written, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AnEntryNotRejected_WritesNothing()
    {
        // Arrange
        var feed = FeedInParts();
        var useCase = new ResubmitRejectedEntry(_store, _sync, _clock);

        // Act
        var written = await useCase.ExecuteAsync(feed.Id, CancellationToken.None);

        // Assert
        written.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ARejectedEntryThatWasDeleted_WritesNothing()
    {
        // Arrange
        var feed = FeedInParts();
        feed.Delete(new HlcStamp(++_wall, 0, Device));
        Reject(feed);
        var useCase = new ResubmitRejectedEntry(_store, _sync, _clock);

        // Act
        var written = await useCase.ExecuteAsync(feed.Id, CancellationToken.None);

        // Assert
        written.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    private void Reject(CareEntry entry) =>
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>())
            .Returns([new SyncRejection(SyncEntityTypes.CareEntry, entry.Id.Value, "entry.timing-shape-not-allowed", Now)]);

    private CareEntry FeedInParts()
    {
        var start = Now.AddMinutes(-60).AddSeconds(17);
        var details = BreastFeed.Create(
            [new BreastSegment(BreastSide.Left, start, start.AddMinutes(10)), new BreastSegment(BreastSide.Left, start.AddMinutes(15), start.AddMinutes(20))],
            isCompleted: true).Value;
        var entry = CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby), details, null,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), Now).Value;
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        return entry;
    }
}
