using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Undo writes only what it must (Spec 007 §5).</summary>
public sealed class UndoTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero));
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public UndoTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task DeleteWithUndoAsync_UndoWithinWindow_WritesNothing()
    {
        // Arrange
        var entry = Diaper();
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        using var undo = Undo();

        // Act
        await undo.DeleteWithUndoAsync(entry.Id, "Diaper");
        _time.Advance(TimeSpan.FromSeconds(3));
        await undo.UndoAsync();
        _time.Advance(TimeSpan.FromSeconds(10));

        // Assert
        await _store.DidNotReceiveWithAnyArgs().DeleteAsync(default, default, default);
        undo.PendingDelete.Should().BeNull();
    }

    [Fact]
    public async Task DeleteWithUndoAsync_WindowPasses_WritesTheTombstone()
    {
        // Arrange
        var entry = Diaper();
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        _store.DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>()).Returns(true);
        using var undo = Undo();

        // Act
        await undo.DeleteWithUndoAsync(entry.Id, "Diaper");
        undo.PendingDelete.Should().Be(entry.Id, "the row is hidden while it waits");
        _time.Advance(UndoService.Window + TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Assert
        await _store.Received(1).DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OfferUndoForLog_Undo_WritesATombstoneForTheNewEntry()
    {
        // Arrange
        var entry = Diaper();
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        _store.DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>()).Returns(true);
        using var undo = Undo();

        // Act
        undo.OfferUndoForLog(entry.Id, "Logged · Diaper");
        await undo.UndoAsync();

        // Assert
        await _store.Received(1).DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OfferUndoForEdit_Undo_WritesThePreviousValuesAsANewRevision()
    {
        // Arrange: the entry was wet; an edit made it dirty.
        var before = Diaper(DiaperContents.Wet);
        var after = Diaper(DiaperContents.Wet, before.Id);
        after.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Dirty), new HlcStamp(++_wall, 0, Device), _time.GetUtcNow());
        _store.FindAsync(before.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(after));
        CareEntry? saved = null;
        await _store.SaveAsync(Arg.Do<CareEntry>(e => saved = e), Arg.Any<CancellationToken>());
        using var undo = Undo();

        // Act
        undo.OfferUndoForEdit(before, "Saved · Diaper");
        await undo.UndoAsync();

        // Assert
        ((DiaperDetails)saved!.Details).Contents.Should().Be(DiaperContents.Wet);
        saved.Version.Should().BeGreaterThan(before.Version, "last-writer-wins needs a newer version, never an older one");
    }

    private UndoService Undo() =>
        new(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), _store, _time);

    private CareEntry Diaper(DiaperContents contents = DiaperContents.Wet, EntryId? id = null) =>
        CareEntry.Create(id ?? new EntryId(Guid.CreateVersion7(_time.GetUtcNow().AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby),
            new DiaperDetails(contents), EntryTiming.Point(_time.GetUtcNow().AddMinutes(-5)), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), _time.GetUtcNow()).Value;
}
