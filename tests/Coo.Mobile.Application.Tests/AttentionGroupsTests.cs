using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011d §4, §7: duplicate pairs and feeds close together become groups; dismissals still hide pairs.</summary>
public sealed class AttentionGroupsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset T0 = Now.AddHours(-4);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId Twin = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IDuplicateDismissalStore _dismissals = Substitute.For<IDuplicateDismissalStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly HashSet<EntryPair> _dismissed = [];
    private long _wall = 1;

    public AttentionGroupsTests()
    {
        _dismissals.ListAsync(Household, Arg.Any<CancellationToken>()).Returns(_dismissed);
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeFeedsWith12MinutePauses_AreOneMergeableGroup()
    {
        // Arrange: starts 22 minutes apart, so no two are duplicates; each starts 12 minutes after the last ended.
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);

        // Act
        var attention = await Attention();

        // Assert
        var group = attention.Groups.Should().ContainSingle().Which;
        group.Entries.Select(e => e.Id).Should().Equal(a.Id, b.Id, c.Id);
        group.OpenPairs.Should().BeEquivalentTo([new EntryPair(a.Id, b.Id), new EntryPair(b.Id, c.Id)]);
        group.IsFeeds.Should().BeTrue();
        group.CanMerge.Should().BeTrue();
        group.MergeRefusal.Should().BeNull();
        attention.Count.Should().Be(1, "a group counts once in the badge");
    }

    [Fact]
    public async Task ExecuteAsync_TwoFeedsCloseOnlyEndToStart_AreAGroupThatCounts()
    {
        // Arrange: starts 25 minutes apart (not a duplicate), a 5 minute pause between them.
        var (a, b) = (Breast(T0, 20), Breast(T0.AddMinutes(25), 8, BreastSide.Right));
        Store(a, b);

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().ContainSingle().Which.Entries.Should().HaveCount(2);
        attention.Count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AFeedCloseOnlyToALongerOne_JoinsTheGroupWhateverTheOrder()
    {
        // Arrange: A 10:00–12:00 holds B 10:10–10:20 and C at 11:50; B and C are 90 minutes apart.
        var a = Breast(T0, 120);
        var b = Breast(T0.AddMinutes(10), 10, BreastSide.Right);
        var c = Breast(T0.AddMinutes(110), 5);
        Store(a, b, c);

        // Act
        var attention = await Attention();

        // Assert
        var group = attention.Groups.Should().ContainSingle().Which;
        group.Entries.Select(e => e.Id).Should().Equal(a.Id, b.Id, c.Id);
        group.CanMerge.Should().BeFalse();
        group.MergeRefusal.Should().Be(FeedMergeRefusal.Overlap);
    }

    [Fact]
    public async Task ExecuteAsync_OnePairOfAGroupKeptBoth_ShowsTheWholeGroupWithTheOtherPairOpen()
    {
        // Arrange
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);
        _dismissed.Add(new EntryPair(b.Id, a.Id));

        // Act
        var attention = await Attention();

        // Assert
        var group = attention.Groups.Should().ContainSingle().Which;
        group.Entries.Should().HaveCount(3);
        group.OpenPairs.Should().Equal(new EntryPair(b.Id, c.Id));
        group.CanMerge.Should().BeTrue();
        attention.Duplicates.Should().ContainSingle().Which.Pair.Should().Be(new EntryPair(b.Id, c.Id));
    }

    [Fact]
    public async Task ExecuteAsync_EveryPairOfAGroupKept_HidesTheGroup()
    {
        // Arrange
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        Store(a, b, c);
        _dismissed.Add(new EntryPair(a.Id, b.Id));
        _dismissed.Add(new EntryPair(b.Id, c.Id));

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().BeEmpty();
        attention.Count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_BreastAndBottleCloseTogether_AreNoGroup()
    {
        // Arrange
        Store(Breast(T0, 10), Bottle(T0.AddMinutes(12)));

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TwinsFedCloseTogether_AreNoGroup()
    {
        // Arrange
        Store(Breast(T0, 10), Breast(T0.AddMinutes(12), 10, baby: Twin));

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_FeedsFarApart_AreNoGroup()
    {
        // Arrange
        Store(Breast(T0, 10), Breast(T0.AddMinutes(26), 10));

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ARunningFeedCloseOnlyEndToStart_IsNoGroupWhileItRuns()
    {
        // Arrange: a 20 minute feed, then the other side started as a new timer 5 minutes after it ended.
        var running = Make(BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-5)), null);
        Store(Breast(Now.AddMinutes(-30), 20), running);

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().BeEmpty("the parent is still feeding; the pair can be offered once the timer stops");
        attention.Count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ARunningFeedThatDuplicatesAnother_IsAGroupThatCannotMerge()
    {
        // Arrange: starts within 15 minutes of the other: a duplicate pair, whatever the timer.
        var running = Make(BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-5)), null);
        Store(Breast(Now.AddMinutes(-20), 10), running);

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().ContainSingle().Which.MergeRefusal.Should().Be(FeedMergeRefusal.Running);
    }

    [Fact]
    public async Task ExecuteAsync_ARejectedFeedInAGroup_CannotMergeAsReadOnly()
    {
        // Arrange
        var (a, b) = (Breast(T0, 10), Breast(T0.AddMinutes(13), 10));
        Store(a, b);
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>())
            .Returns([new SyncRejection(SyncEntityTypes.CareEntry, b.Id.Value, "breast.too-long", Now)]);
        _store.FindAsync(b.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(b));

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Should().ContainSingle().Which.MergeRefusal.Should().Be(FeedMergeRefusal.ReadOnly);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeDiapersInAChain_AreOneGroupThatIsNotAFeedMerge()
    {
        // Arrange: diapers are duplicates within 10 minutes; a–b and b–c match, a–c do not.
        var (a, b, c) = (Diaper(T0), Diaper(T0.AddMinutes(7)), Diaper(T0.AddMinutes(14)));
        Store(a, b, c);

        // Act
        var attention = await Attention();

        // Assert
        var group = attention.Groups.Should().ContainSingle().Which;
        group.Entries.Should().HaveCount(3);
        group.IsFeeds.Should().BeFalse();
        group.CanMerge.Should().BeFalse();
        group.MergeRefusal.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TwoGroups_NewestFirst()
    {
        // Arrange
        var (a, b) = (Breast(T0, 10), Breast(T0.AddMinutes(13), 10));
        var (c, d) = (Breast(T0.AddHours(2), 10), Breast(T0.AddHours(2).AddMinutes(13), 10));
        Store(a, b, c, d);

        // Act
        var attention = await Attention();

        // Assert
        attention.Groups.Select(g => g.Entries[0].Id).Should().Equal(c.Id, a.Id);
    }

    [Fact]
    public async Task KeepAll_AGroupsOpenPairs_DismissesEachOfThem()
    {
        // Arrange
        var (a, b, c) = (Breast(T0, 10), Breast(T0.AddMinutes(22), 10), Breast(T0.AddMinutes(44), 10));
        var clock = Substitute.For<IVersionClock>();
        clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        var saved = new List<DuplicateDismissal>();
        _dismissals.SaveAsync(Arg.Do<DuplicateDismissal>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var dismiss = new DismissDuplicate(_dismissals, clock, new FakeTimeProvider(Now));

        // Act
        await dismiss.ExecuteAsync(Household, [new EntryPair(a.Id, b.Id), new EntryPair(b.Id, c.Id)], CancellationToken.None);

        // Assert
        saved.Select(d => d.Pair).Should().Equal(new EntryPair(a.Id, b.Id), new EntryPair(b.Id, c.Id));
    }

    private Task<AttentionSummary> Attention() =>
        new GetAttention(_store, _dismissals, _sync).ExecuteAsync(Household, Now, CancellationToken.None);

    private void Store(params CareEntry[] entries) =>
        _store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([.. entries.OrderByDescending(e => e.Timing.OccurredAt).Select(e => new LiveEntry(e))]);

    private CareEntry Breast(DateTimeOffset start, int minutes, BreastSide side = BreastSide.Left, BabyId? baby = null) =>
        Make(BreastFeed.Create([new BreastSegment(side, start, start.AddMinutes(minutes))], isCompleted: true).Value, null, baby);

    private CareEntry Bottle(DateTimeOffset at) =>
        Make(BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(60).Value).Value, EntryTiming.Point(at));

    private CareEntry Diaper(DateTimeOffset at) => Make(new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(at));

    private CareEntry Make(EntryDetails details, EntryTiming? timing, BabyId? baby = null) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(T0.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(baby ?? Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;
}
