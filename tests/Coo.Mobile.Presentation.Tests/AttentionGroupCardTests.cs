using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011d §4: the group card's actions per case, and the toast after a merge.</summary>
[Collection(nameof(StringsState))]
public sealed class AttentionGroupCardTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset T0 = Now.AddHours(-3);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IDuplicateDismissalStore _dismissals = Substitute.For<IDuplicateDismissalStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly List<UndoToast?> _toasts = [];
    private UndoService? _undo;
    private long _wall = 1;

    public AttentionGroupCardTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Phone));
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Phone)).Value]);
        _dismissals.ListAsync(Household, Arg.Any<CancellationToken>()).Returns(new HashSet<EntryPair>());
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>()).Returns([]);
        _store.WriteAllAsync(Arg.Any<CareLogWrites>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose()
    {
        _undo?.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    [Fact]
    public async Task RefreshAsync_ThreeFeedsCloseTogether_ShowOneFeedGroupThatCanMerge()
    {
        // Arrange: 10 minutes each, 12 minute pauses.
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10, BreastSide.Right), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var group = model.FeedGroups.Should().ContainSingle().Which;
        group.Members.Select(m => m.Text).Should().Equal(
            "11:00 · Breast · L\u00A010\u00A0m", "11:22 · Breast · R\u00A010\u00A0m", "11:44 · Breast · L\u00A010\u00A0m");
        group.Merge.Should().NotBeNull();
        group.Why.Should().BeNull();
        model.OtherGroups.Should().BeEmpty();
        model.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task MergeCommand_AMergeableGroup_WritesOnceAndOffersUndo()
    {
        // Arrange
        var (a, b) = (Breast(T0, 10), Breast(T0.AddMinutes(13), 5, BreastSide.Right));
        Store(a, b);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.FeedGroups[0].Merge!.ExecuteAsync(null);

        // Assert
        await _store.Received(1).WriteAllAsync(Arg.Is<CareLogWrites>(w => w.Saves.Count == 1 && w.Deletes.Count == 1), Arg.Any<CancellationToken>());
        _toasts.Should().ContainSingle().Which.Should().Be(new UndoToast("Merged into one feed", CanUndo: true));
    }

    [Fact]
    public async Task MergeCommand_TheStoreFails_SaysSoWithoutThrowing()
    {
        // Arrange
        var (a, b) = (Breast(T0, 10), Breast(T0.AddMinutes(13), 5, BreastSide.Right));
        Store(a, b);
        _store.WriteAllAsync(Arg.Any<CareLogWrites>(), Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new IOException("disk full"));
        var model = Model();
        await model.RefreshAsync();

        // Act
        Func<Task> act = () => model.FeedGroups[0].Merge!.ExecuteAsync(null);

        // Assert
        await act.Should().NotThrowAsync();
        _toasts.Should().ContainSingle().Which.Should().Be(new UndoToast("That could not be saved.", CanUndo: false));
    }

    [Fact]
    public async Task RefreshAsync_OverlappingFeeds_OfferNoMergeAndSayWhy()
    {
        // Arrange: the same feed logged twice, a few minutes apart.
        var (a, b) = (Breast(T0, 20), Breast(T0.AddMinutes(5), 20));
        Store(a, b);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var group = model.FeedGroups.Should().ContainSingle().Which;
        group.Merge.Should().BeNull();
        group.Why.Should().Be("These feeds overlap, so they can't be merged.");
        group.Members.Should().HaveCount(2).And.OnlyContain(m => m.KeepOnly != null);
        group.KeepAll.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshAsync_NotesTooLongToJoin_OfferNoMergeAndSayWhy()
    {
        // Arrange: two notes of 300 characters cannot become one note of at most 500.
        var (a, b) = (Breast(T0, 10, note: new string('a', 300)), Breast(T0.AddMinutes(13), 5, note: new string('b', 300)));
        Store(a, b);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var group = model.FeedGroups.Should().ContainSingle().Which;
        group.Merge.Should().BeNull();
        group.Why.Should().Be("The notes are too long to join into one.");
    }

    [Fact]
    public async Task RefreshAsync_ARunningDuplicate_OffersNoMergeAndNoReason()
    {
        // Arrange
        var running = Make(BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-5)), null);
        Store(Breast(Now.AddMinutes(-15), 5), running);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var group = model.FeedGroups.Should().ContainSingle().Which;
        group.Merge.Should().BeNull();
        group.Why.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_TwoDiapersLoggedTwice_AreAnOtherGroupWithoutMerge()
    {
        // Arrange
        Store(Diaper(T0), Diaper(T0.AddMinutes(4)));
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.FeedGroups.Should().BeEmpty();
        var group = model.OtherGroups.Should().ContainSingle().Which;
        group.Merge.Should().BeNull();
        group.Why.Should().BeNull();
    }

    [Fact]
    public async Task KeepOnlyCommand_InAGroupOfThree_DeletesTheOtherTwoUnderOneUndo()
    {
        // Arrange
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.FeedGroups[0].Members[1].KeepOnly.ExecuteAsync(null);
        await model.RefreshAsync();

        // Assert
        _toasts.Should().ContainSingle().Which.Should().Be(new UndoToast("Deleted · 2 entries", CanUndo: true));
        _undo!.IsPendingDelete(a.Id).Should().BeTrue();
        _undo.IsPendingDelete(c.Id).Should().BeTrue();
        _undo.IsPendingDelete(b.Id).Should().BeFalse();
        model.FeedGroups.Should().BeEmpty("the group waits for its deletes");
    }

    [Fact]
    public async Task KeepOnlyCommand_InAPair_NamesTheOtherInTheToast()
    {
        // Arrange
        var (a, b) = (Breast(T0, 10), Breast(T0.AddMinutes(13), 5, BreastSide.Right));
        Store(a, b);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.FeedGroups[0].Members[0].KeepOnly.ExecuteAsync(null);

        // Assert
        _toasts.Should().ContainSingle().Which!.Text.Should().Be("Deleted · Breast · R\u00A05\u00A0m");
        _undo!.IsPendingDelete(b.Id).Should().BeTrue();
    }

    [Fact]
    public async Task KeepAllCommand_AGroupOfThree_DismissesEachOpenPair()
    {
        // Arrange
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);
        var saved = new List<DuplicateDismissal>();
        _dismissals.SaveAsync(Arg.Do<DuplicateDismissal>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.FeedGroups[0].KeepAll.ExecuteAsync(null);

        // Assert
        saved.Select(d => d.Pair).Should().BeEquivalentTo([new EntryPair(a.Id, b.Id), new EntryPair(b.Id, c.Id)]);
    }

    private AttentionViewModel Model()
    {
        var clockFormat = Substitute.For<IClockFormat>();
        clockFormat.Is24Hour.Returns(true);
        _undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), new UndoFeedMerge(_store, _clock, _time), _store, _time);
        _undo.ToastChanged += (_, t) => _toasts.Add(t);
        return new AttentionViewModel(
            _households, new GetAttention(_store, _dismissals, _sync), new DismissDuplicate(_dismissals, _clock, _time),
            new MergeFeeds(_store, _sync, _clock, _time), _undo, clockFormat, _time);
    }

    private void Store(params CareEntry[] entries)
    {
        _store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([.. entries.OrderByDescending(e => e.Timing.OccurredAt).Select(e => new LiveEntry(e))]);
        foreach (var entry in entries)
            _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
    }

    private CareEntry Breast(DateTimeOffset start, int minutes, BreastSide side = BreastSide.Left, string? note = null) =>
        Make(BreastFeed.Create([new BreastSegment(side, start, start.AddMinutes(minutes))], isCompleted: true).Value, null, note);

    private CareEntry Diaper(DateTimeOffset at) => Make(new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming? timing, string? note = null) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(T0.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Phone), Now, Note.From(note).Value).Value;
}
