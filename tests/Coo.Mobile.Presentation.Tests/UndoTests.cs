using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Undo writes only what it must (Spec 007 §5).</summary>
[Collection(nameof(StringsState))]
public sealed class UndoTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero));
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public UndoTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

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
        undo.IsPendingDelete(entry.Id).Should().BeFalse();
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
        undo.IsPendingDelete(entry.Id).Should().BeTrue("the row is hidden while it waits");
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
    public async Task OfferUndoForLog_OneLogForTwins_UndoTakesBothBack()
    {
        // Arrange
        var lena = Diaper();
        var mia = Diaper();
        foreach (var entry in new[] { lena, mia })
        {
            _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
            _store.DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        using var undo = Undo();

        // Act
        undo.OfferUndoForLog([lena.Id, mia.Id], "Logged · Diaper · Lena, Mia");
        await undo.UndoAsync();

        // Assert
        await _store.Received(1).DeleteAsync(lena.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(mia.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task OfferUndoForMerge_Undo_PutsTheMergeBackInOneWrite()
    {
        // Arrange: the merge made the earliest entry dirty and deleted the other.
        var before = Diaper(DiaperContents.Wet);
        var merged = Diaper(DiaperContents.Wet, before.Id);
        merged.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Dirty), new HlcStamp(++_wall, 0, Device), _time.GetUtcNow());
        var gone = Diaper();
        gone.Delete(new HlcStamp(++_wall, 0, Device));
        _store.FindAsync(before.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(merged));
        _store.FindAsync(gone.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(gone));
        CareLogWrites? written = null;
        _store.WriteAllAsync(Arg.Do<CareLogWrites>(w => written = w), Arg.Any<CancellationToken>()).Returns(true);
        UndoToast? toast = null;
        using var undo = Undo();
        undo.ToastChanged += (_, t) => toast ??= t;

        // Act
        undo.OfferUndoForMerge(new FeedMergeReceipt(before.Id, before.Details, before.Note, [gone.Id], merged.Version), "Merged into one feed");
        await undo.UndoAsync();

        // Assert
        toast.Should().Be(new UndoToast("Merged into one feed", CanUndo: true));
        ((DiaperDetails)written!.Saves.Should().ContainSingle().Which.Details).Contents.Should().Be(DiaperContents.Wet);
        written.Restores.Select(r => r.Id).Should().Equal(gone.Id);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task OfferUndoForMerge_WindowPasses_WritesNothing()
    {
        // Arrange
        var entry = Diaper();
        using var undo = Undo();

        // Act
        undo.OfferUndoForMerge(new FeedMergeReceipt(entry.Id, entry.Details, null, [new EntryId(Guid.CreateVersion7(_time.GetUtcNow()))], entry.Version), "Merged into one feed");
        _time.Advance(UndoService.Window + TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        await undo.UndoAsync();

        // Assert
        await _store.DidNotReceiveWithAnyArgs().WriteAllAsync(default!, default);
    }

    [Fact]
    public async Task OfferUndoForMerge_UndoThatWritesNothing_SaysSoInsteadOfUndone()
    {
        // Arrange: the merged feed was deleted and the other is back already, so there is nothing to put back.
        var merged = Diaper();
        merged.Delete(new HlcStamp(++_wall, 0, Device));
        var back = Diaper();
        _store.FindAsync(merged.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(merged));
        _store.FindAsync(back.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(back));
        var toasts = new List<UndoToast?>();
        using var undo = Undo();
        undo.ToastChanged += (_, t) => toasts.Add(t);

        // Act
        undo.OfferUndoForMerge(new FeedMergeReceipt(merged.Id, merged.Details, null, [back.Id], merged.Version), "Merged into one feed");
        await undo.UndoAsync();

        // Assert
        await _store.DidNotReceiveWithAnyArgs().WriteAllAsync(default!, default);
        toasts[^1].Should().Be(new UndoToast("Nothing was undone: it had already changed.", CanUndo: false));
    }

    [Fact]
    public async Task DeleteWithUndoAsync_SeveralEntries_HidesEachAndUndoWritesNothing()
    {
        // Arrange: "Keep only this one" in a group of three deletes the other two under one undo.
        var (a, b) = (Diaper(), Diaper());
        UndoToast? toast = null;
        using var undo = Undo();
        undo.ToastChanged += (_, t) => toast ??= t;

        // Act
        await undo.DeleteWithUndoAsync([a.Id, b.Id], "ignored for several");
        var pending = (undo.IsPendingDelete(a.Id), undo.IsPendingDelete(b.Id));
        await undo.UndoAsync();

        // Assert
        pending.Should().Be((true, true));
        toast.Should().Be(new UndoToast("Deleted · 2 entries", CanUndo: true));
        undo.IsPendingDelete(a.Id).Should().BeFalse();
        undo.IsPendingDelete(b.Id).Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!, default);
    }

    [Fact]
    public async Task DeleteWithUndoAsync_SeveralEntriesWindowPasses_WritesEveryTombstone()
    {
        // Arrange
        var (a, b) = (Diaper(), Diaper());
        foreach (var entry in new[] { a, b })
        {
            _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
            _store.DeleteAsync(entry.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        using var undo = Undo();

        // Act
        await undo.DeleteWithUndoAsync([a.Id, b.Id], "ignored for several");
        _time.Advance(UndoService.Window + TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Assert
        await _store.Received(1).DeleteAsync(a.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(b.Id, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
        undo.IsPendingDelete(a.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteWithUndoAsync_OneEntryOfACollection_NamesIt()
    {
        // Arrange
        var a = Diaper();
        UndoToast? toast = null;
        using var undo = Undo();
        undo.ToastChanged += (_, t) => toast ??= t;

        // Act
        await undo.DeleteWithUndoAsync([a.Id], "Diaper · Wet");

        // Assert
        toast.Should().Be(new UndoToast("Deleted · Diaper · Wet", CanUndo: true));
    }

    private UndoService Undo() =>
        new(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), new UndoFeedMerge(_store, _clock, _time), _store, _time);

    private CareEntry Diaper(DiaperContents contents = DiaperContents.Wet, EntryId? id = null) =>
        CareEntry.Create(id ?? new EntryId(Guid.CreateVersion7(_time.GetUtcNow().AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby),
            new DiaperDetails(contents), EntryTiming.Point(_time.GetUtcNow().AddMinutes(-5)), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), _time.GetUtcNow()).Value;
}
