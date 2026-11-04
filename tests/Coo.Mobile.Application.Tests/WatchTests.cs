using Coo.Contracts.Sync;
using Coo.Contracts.Wearables;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Wearables;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>ADR 0009 §2–§3: states from the watch through the phone's write path, and the snapshot it shows.</summary>
public sealed class WatchTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Laura = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly Guid Watch = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 3, 10, 0, TimeSpan.Zero);

    private readonly VersionedStore _store = new();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly FakeTimeProvider _time = new(Now);

    public WatchTests()
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>())
            .Returns([HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Phone)).Value]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([Baby(Laura, "Laura")]);
    }

    [Fact]
    public async Task Execute_AWetDiaperFromTheWatch_SavesItWithTheWatchsIdVersionAndSource()
    {
        // Arrange
        var state = Diaper(Guid.Parse("019a4f2e-8c00-7000-8000-000000000001"), wall: 100);

        // Act
        var result = await Apply().ExecuteAsync(state, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(WatchApplyOutcome.Applied);
        var saved = _store.Live.Should().ContainSingle().Subject;
        saved.Id.Value.Should().Be(state.EntryId);
        saved.Version.Should().Be(new HlcStamp(100, 0, new DeviceId(Watch)));
        saved.Source.Should().Be(EntrySource.Watch);
        saved.Details.Should().Be(new DiaperDetails(DiaperContents.Wet));
    }

    [Fact]
    public async Task Execute_TheDeleteArrivesBeforeTheCreate_TheCreateNeverBringsItBack()
    {
        // Arrange
        var id = Guid.Parse("019a4f2e-8c00-7000-8000-000000000001");
        await Apply().ExecuteAsync(Diaper(id, wall: 100), CancellationToken.None);

        // Act: the delete (newer), then the same create delivered again (a duplicate).
        var deleted = await Apply().ExecuteAsync(Diaper(id, wall: 200) with { Deleted = true }, CancellationToken.None);
        var late = await Apply().ExecuteAsync(Diaper(id, wall: 100), CancellationToken.None);

        // Assert
        deleted.Outcome.Should().Be(WatchApplyOutcome.Applied);
        late.Outcome.Should().Be(WatchApplyOutcome.Superseded);
        _store.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ASleepStartedOnThePhoneStoppedOnTheWatch_EndsItThere()
    {
        // Arrange
        var sleep = CareEntry.Create(
            new EntryId(Guid.Parse("019a4f2e-8c00-7000-8000-000000000009")), Household, EntrySubject.Baby(Laura), new SleepDetails(),
            EntryTiming.Session(TimeRange.Open(Now.AddHours(-2))), PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(50, 0, Phone), Now).Value;
        _store.Seed(sleep);
        var stop = new WatchEntryStateV1(1, sleep.Id.Value, Laura.Value, "sleep", false, Now.AddHours(-2).ToUnixTimeMilliseconds(),
            Now.ToUnixTimeMilliseconds(), new HlcStampDto(300, 0, Watch));

        // Act
        var result = await Apply().ExecuteAsync(stop, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(WatchApplyOutcome.Applied);
        var ended = _store.Live.Single();
        ended.Timing.IsInProgress.Should().BeFalse();
        ((SessionTiming)ended.Timing).Range.End.Should().Be(Now);
    }

    [Theory]
    [InlineData("crying", "watch.kind")]
    [InlineData("diaper", "watch.contents")]
    public async Task Execute_AnUnknownKindOrContents_IsRejectedAndNothingWritten(string kind, string reason)
    {
        // Arrange
        var state = Diaper(Guid.Parse("019a4f2e-8c00-7000-8000-000000000001"), wall: 100) with { Kind = kind, Contents = "rainbow" };

        // Act
        var result = await Apply().ExecuteAsync(state, CancellationToken.None);

        // Assert
        result.Should().Be(new WatchApplyResult(WatchApplyOutcome.Rejected, reason));
        _store.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ABabyNotInTheFamilyOrAFutureSchema_IsRejected()
    {
        // Act
        var stranger = await Apply().ExecuteAsync(Diaper(Guid.Parse("019a4f2e-8c00-7000-8000-000000000001"), 100) with { BabyId = Guid.Parse("7a1c0000-0000-7000-8000-0000000000bf") }, CancellationToken.None);
        var future = await Apply().ExecuteAsync(Diaper(Guid.Parse("019a4f2e-8c00-7000-8000-000000000001"), 100) with { SchemaVersion = 9 }, CancellationToken.None);

        // Assert
        stranger.Reason.Should().Be("watch.baby");
        future.Reason.Should().Be("watch.schema");
    }

    [Fact]
    public void Build_TwinsWithARunningFeed_ShowsTheFocusedBabyFirstAndTheFeedsSide()
    {
        // Arrange
        var feed = CareEntry.Create(
            new EntryId(Guid.Parse("019a4f2e-8c00-7000-8000-000000000005")), Household, EntrySubject.Baby(Laura), BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-8)),
            null, PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(1, 0, Phone), Now).Value;
        var today = new TodaySummary([feed], feed, null, null, null, Now.AddHours(-1), [], DayMath.Day(new DateOnly(2026, 11, 4), TimeZoneInfo.Utc));
        var household = HouseholdSettings.Create(Household, VolumeUnit.Ounces, "Europe/Amsterdam", new HlcStamp(1, 0, Phone)).Value;

        // Act
        var snapshot = WatchSnapshots.Build(household, [Baby(Laura, "Laura"), Baby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2")), "Mia")], today, null, "pl", Now);

        // Assert
        snapshot.SelectedBabyId.Should().Be(Laura.Value);
        snapshot.Babies.Select(b => b.Name).Should().Equal("Laura", "Mia");
        snapshot.Running.Should().ContainSingle().Which.Should().Be(new WatchRunningV1(feed.Id.Value, Laura.Value, "breast", Now.AddMinutes(-8).ToUnixTimeMilliseconds(), "right"));
        (snapshot.LastSide, snapshot.VolumeUnit, snapshot.Language, snapshot.NextNap).Should().Be(("right", "oz", "pl", (WatchNapWindowV1?)null));
    }

    private ApplyWatchState Apply() => new(_store, _households, _time);

    private static WatchEntryStateV1 Diaper(Guid id, long wall) =>
        new(1, id, Laura.Value, "diaper", false, Now.AddMinutes(-1).ToUnixTimeMilliseconds(), null, new HlcStampDto(wall, 0, Watch), Contents: "wet");

    private static BabyProfile Baby(BabyId id, string name) =>
        BabyProfile.Create(id, Household, name, [], new DateOnly(2026, 5, 2), null, new HlcStamp(1, 0, Phone), new DateOnly(2026, 11, 4)).Value;

    /// <summary>Entries with their versions and tombstones, as the SQLite store keeps them.</summary>
    private sealed class VersionedStore : ICareLogStore
    {
        private readonly Dictionary<EntryId, CareEntry> _live = [];
        private readonly Dictionary<EntryId, HlcStamp> _tombstones = [];

        public IReadOnlyList<CareEntry> Live => [.. _live.Values];

        public void Seed(CareEntry entry) => _live[entry.Id] = entry;

        public Task SaveAsync(CareEntry entry, CancellationToken cancellationToken)
        {
            _live[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken) =>
            Task.FromResult<StoredEntry?>(_live.TryGetValue(id, out var e) ? new LiveEntry(e) : null);

        public Task<(HlcStamp Version, bool IsDeleted)?> FindVersionAsync(EntryId id, CancellationToken cancellationToken) =>
            Task.FromResult<(HlcStamp, bool)?>(_tombstones.TryGetValue(id, out var gone) ? (gone, true) : _live.TryGetValue(id, out var e) ? (e.Version, false) : null);

        public Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken)
        {
            if (!_live.Remove(id))
                return Task.FromResult(false);
            _tombstones[id] = version;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredEntry>>([.. _live.Values.Select(e => new LiveEntry(e))]);

        public Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountAllEntriesAsync(HouseholdId household, CancellationToken cancellationToken) => Task.FromResult(_live.Count);
    }
}
