using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011d §3, §7: a merge is one write; undo puts everything back. Only the outbound ports are mocked.</summary>
public sealed class MergeFeedsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset T0 = Now.AddHours(-2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly FixedTime _time = new(Now);
    private readonly List<CareLogWrites> _written = [];
    private long _wall = 1_000;

    public MergeFeedsTests()
    {
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        _sync.ListRejectionsAsync(Arg.Any<HouseholdId>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.WriteAllAsync(Arg.Do<CareLogWrites>(_written.Add), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeBreastFeedsCloseTogether_RevisesTheEarliestAndDeletesTheRestInOneWrite()
    {
        // Arrange: 10 min, a 3 min pause, 5 min, a 2 min pause, 4 min; notes on the first and the last.
        var a = Stored(Breast(T0, 10, note: "fussy"));
        var b = Stored(Breast(T0.AddMinutes(13), 5, BreastSide.Right));
        var c = Stored(Breast(T0.AddMinutes(20), 4, note: "slept after"));
        var before = a.Version;

        // Act
        var result = await Merge().ExecuteAsync([c.Id, a.Id, b.Id], CancellationToken.None);

        // Assert
        result.IsMerged.Should().BeTrue();
        result.MergedId.Should().Be(a.Id);
        _written.Should().ContainSingle("the revision and the tombstones go in one transaction");
        var write = _written[0];
        var merged = write.Saves.Should().ContainSingle().Which;
        merged.Id.Should().Be(a.Id);
        var breast = merged.Details.Should().BeOfType<BreastFeed>().Which;
        breast.Segments.Should().HaveCount(3);
        breast.TotalDuration.Should().Be(TimeSpan.FromMinutes(19));
        merged.Timing.Should().Be(EntryTiming.Session(TimeRange.Create(T0, T0.AddMinutes(24)).Value));
        merged.Note!.Text.Should().Be("fussy · slept after");
        merged.Version.Should().BeGreaterThan(before);
        write.Deletes.Select(d => d.Id).Should().Equal(b.Id, c.Id);
        write.Deletes.Select(d => d.Version).Should().OnlyHaveUniqueItems().And.OnlyContain(v => v > merged.Version);
        write.Restores.Should().BeEmpty();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!, default);
        result.Merged!.PreviousDetails.Should().BeOfType<BreastFeed>().Which.Segments.Should().ContainSingle();
        result.Merged.PreviousNote!.Text.Should().Be("fussy");
        result.Merged.DeletedIds.Should().Equal(b.Id, c.Id);
    }

    [Fact]
    public async Task ExecuteAsync_TwoBottlesCloseTogether_SumsTheMilkOnTheEarliest()
    {
        // Arrange
        var a = Stored(Bottle(T0, Milk.Formula, 60));
        var b = Stored(Bottle(T0.AddMinutes(10), Milk.Formula, 30));

        // Act
        var result = await Merge().ExecuteAsync([a.Id, b.Id], CancellationToken.None);

        // Assert
        result.MergedId.Should().Be(a.Id);
        var merged = _written.Should().ContainSingle().Which.Saves.Should().ContainSingle().Which;
        merged.Details.Should().BeOfType<BottleFeed>().Which.Total.Millilitres.Should().Be(90);
        merged.Timing.OccurredAt.Should().Be(T0);
    }

    [Fact]
    public async Task ExecuteAsync_OverlappingFeeds_RefusesAndWritesNothing()
    {
        // Arrange
        var a = Stored(Breast(T0, 10));
        var b = Stored(Breast(T0.AddMinutes(5), 10, BreastSide.Right));

        // Act
        var result = await Merge().ExecuteAsync([a.Id, b.Id], CancellationToken.None);

        // Assert
        result.IsMerged.Should().BeFalse();
        result.Refusal.Should().Be(FeedMergeRefusal.Overlap);
        await _store.DidNotReceiveWithAnyArgs().WriteAllAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_AFeedTheServerRejected_RefusesAsReadOnly()
    {
        // Arrange
        var a = Stored(Breast(T0, 10));
        var b = Stored(Breast(T0.AddMinutes(12), 5));
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>())
            .Returns([new SyncRejection(SyncEntityTypes.CareEntry, b.Id.Value, "breast.too-long", Now)]);

        // Act
        var result = await Merge().ExecuteAsync([a.Id, b.Id], CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(FeedMergeRefusal.ReadOnly);
        await _store.DidNotReceiveWithAnyArgs().WriteAllAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_AQuarantinedEntry_RefusesAsReadOnly()
    {
        // Arrange
        var a = Stored(Breast(T0, 10));
        var broken = new EntryId(Guid.CreateVersion7(T0));
        _store.FindAsync(broken, Arg.Any<CancellationToken>()).Returns(new QuarantinedEntry(broken, "feeding", T0.AddMinutes(12), "breast.no-segments"));

        // Act
        var result = await Merge().ExecuteAsync([a.Id, broken], CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(FeedMergeRefusal.ReadOnly);
        await _store.DidNotReceiveWithAnyArgs().WriteAllAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_AFeedDeletedBeforeTheWrite_RefusesAsReadOnly()
    {
        // Arrange: the other phone's tombstone lands between the read and the write.
        var a = Stored(Breast(T0, 10));
        var b = Stored(Breast(T0.AddMinutes(12), 5));
        _store.WriteAllAsync(Arg.Any<CareLogWrites>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await Merge().ExecuteAsync([a.Id, b.Id], CancellationToken.None);

        // Assert
        result.Refusal.Should().Be(FeedMergeRefusal.ReadOnly);
    }

    [Fact]
    public async Task ExecuteAsync_OneFeed_Throws()
    {
        // Arrange
        var a = Stored(Breast(T0, 10));

        // Act
        Func<Task> act = async () => await Merge().ExecuteAsync([a.Id], CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UndoAsync_AfterAMerge_RestoresTheEarliestAndBringsTheOthersBackInOneWrite()
    {
        // Arrange
        var a = Stored(Breast(T0, 10, note: "fussy"));
        var b = Stored(Breast(T0.AddMinutes(13), 5, note: "burped"));
        var c = Stored(Breast(T0.AddMinutes(20), 4));
        var original = a.Details;
        var merged = (await Merge().ExecuteAsync([a.Id, b.Id, c.Id], CancellationToken.None)).Merged!;
        AsDeleted(b);
        AsDeleted(c);
        var mergedVersion = a.Version;
        _written.Clear();

        // Act
        await Undo().ExecuteAsync(merged, CancellationToken.None);

        // Assert
        var write = _written.Should().ContainSingle("the undo is one transaction too").Which;
        var restored = write.Saves.Should().ContainSingle().Which;
        restored.Id.Should().Be(a.Id);
        restored.Details.Should().Be(original);
        restored.Note!.Text.Should().Be("fussy");
        restored.Timing.Should().Be(EntryTiming.Session(TimeRange.Create(T0, T0.AddMinutes(10)).Value));
        restored.Version.Should().BeGreaterThan(mergedVersion);
        write.Restores.Select(r => r.Id).Should().Equal(b.Id, c.Id);
        write.Restores.Select(r => r.Version).Should().OnlyHaveUniqueItems().And.OnlyContain(v => v > restored.Version);
        write.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task UndoAsync_AFeedAlreadyBack_RestoresOnlyTheOthers()
    {
        // Arrange: b came back already (the other phone, say); c is still deleted.
        var a = Stored(Breast(T0, 10));
        var b = Stored(Breast(T0.AddMinutes(13), 5));
        var c = Stored(Breast(T0.AddMinutes(20), 4));
        var merged = (await Merge().ExecuteAsync([a.Id, b.Id, c.Id], CancellationToken.None)).Merged!;
        AsDeleted(c);
        _written.Clear();

        // Act
        await Undo().ExecuteAsync(merged, CancellationToken.None);

        // Assert
        _written.Should().ContainSingle().Which.Restores.Select(r => r.Id).Should().Equal(c.Id);
    }

    [Fact]
    public async Task UndoAsync_TheMergedFeedWasDeleted_StillBringsTheOthersBack()
    {
        // Arrange
        var a = Stored(Breast(T0, 10));
        var b = Stored(Breast(T0.AddMinutes(13), 5));
        var merged = (await Merge().ExecuteAsync([a.Id, b.Id], CancellationToken.None)).Merged!;
        AsDeleted(a);
        AsDeleted(b);
        _written.Clear();

        // Act
        await Undo().ExecuteAsync(merged, CancellationToken.None);

        // Assert
        var write = _written.Should().ContainSingle().Which;
        write.Saves.Should().BeEmpty("a deleted feed cannot be revised");
        write.Restores.Select(r => r.Id).Should().Equal(b.Id);
    }

    private MergeFeeds Merge() => new(_store, _sync, _clock, _time);

    private UndoFeedMerge Undo() => new(_store, _clock, _time);

    private CareEntry Stored(CareEntry entry)
    {
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        return entry;
    }

    /// <summary>The store as it is after the merge's tombstone: the row reads back deleted.</summary>
    private void AsDeleted(CareEntry entry)
    {
        entry.Delete(new HlcStamp(++_wall, 0, Device));
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
    }

    private CareEntry Breast(DateTimeOffset start, int minutes, BreastSide side = BreastSide.Left, string? note = null) =>
        Make(BreastFeed.Create([new BreastSegment(side, start, start.AddMinutes(minutes))], isCompleted: true).Value, null, note);

    private CareEntry Bottle(DateTimeOffset at, Milk milk, decimal ml) =>
        Make(BottleFeed.Of(milk, Volume.FromMillilitres(ml).Value).Value, EntryTiming.Point(at), null);

    private CareEntry Make(EntryDetails details, EntryTiming? timing, string? note) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(T0.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now, Note.From(note).Value).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
