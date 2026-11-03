using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §6.1–§6.2: move a never-synced source, copy a synced one, then the question about the old family.</summary>
public sealed class BringFamilyEntriesOverTests : IDisposable
{
    private static readonly BabyId MyLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId TheirLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public BringFamilyEntriesOverTests()
    {
        _account = new Account(_sessions);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new LocalBaby(TheirLena, "Lena")]));
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 4, Phone));
        _records.CountRecordsAsync(Home, Arg.Any<CancellationToken>()).Returns(45);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
        _sync.RunExclusiveAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<Func<CancellationToken, Task>>()(c.Arg<CancellationToken>()));
    }

    public void Dispose() => _account.Dispose();

    private BringFamilyEntriesOver UseCase() => new(_sessions, _account, _moves, _records, _store, _api, _clock,
        new Families(_account, _store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, Substitute.For<ISyncState>(), _records,
            new ProvisionalSettings(_clock, Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now))), _sync);

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    private void MemberOf(HouseholdId household, Role role, int members) => _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(
        [new FamilyMembership(household, role, Now, "Kasia", members), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);

    /// <summary>A never-synced source whose move commits and leaves it empty, owned by her alone.</summary>
    private void MovableAdoptedFamily()
    {
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(true);
        MemberOf(Home, Role.Owner, 1);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]), new LocalSummary(0, []));
    }

    [Fact]
    public async Task PrepareAsync_TwoFamilies_OffersTheSourceSummaryAndTheTargetsBabies()
    {
        // Act
        var choice = await UseCase().PrepareAsync(Home, Joined, CancellationToken.None);

        // Assert
        choice.Local.EntryCount.Should().Be(42);
        choice.Family.Should().ContainSingle().Which.Id.Should().Be(TheirLena);
    }

    [Fact]
    public async Task ExecuteAsync_NeverSyncedSource_MovesItAndLeavesTheEmptyAdoptedFamily()
    {
        // Arrange: spec §6.1. Her adopted family, emptied by the move, is left with confirm: false.
        await SignInAsync();
        MovableAdoptedFamily();

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyLena] = TheirLena }, CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.Moved, 0, 0, OldFamilyQuestion.None));
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.From == Home && p.To == Joined && p.Babies[MyLena] == TheirLena), Me, Arg.Any<CancellationToken>());
        await _api.Received(1).LeaveAsync(Home, false, Arg.Any<CancellationToken>());
        await _moves.Received(1).DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_EmptiedFamilyHasRecordsOnTheServer_StaysAsASeparateFamily()
    {
        // Arrange: another phone of hers had pushed into it: household.confirmation-required.
        await SignInAsync();
        MovableAdoptedFamily();
        _api.LeaveAsync(Home, false, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.ConfirmationRequired));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _moves.DidNotReceive().DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LeaveGetsNoAnswer_KeepsTheFamilyAndStillReportsTheMove()
    {
        // Arrange
        await SignInAsync();
        MovableAdoptedFamily();
        _api.LeaveAsync(Home, false, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _moves.DidNotReceive().DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SignedOutAndInWhileLeaving_TakesNothingOffThePhone()
    {
        // Arrange: the leave's answer belongs to the old sign-in (the Increment 11 generation rule).
        await SignInAsync();
        MovableAdoptedFamily();
        _api.LeaveAsync(Home, false, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            await SignInAsync();
            return LeaveOutcome.Deleted;
        });

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _moves.DidNotReceive().DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
    }

    [Theory]
    [InlineData(Role.Owner, 2)]
    [InlineData(Role.Parent, 2)]
    public async Task ExecuteAsync_MovedFromAFamilyNotHersAlone_LeavesNothing(Role role, int members)
    {
        // Arrange: only her own adopted family (Owner, the only member) is left automatically.
        await SignInAsync();
        MovableAdoptedFamily();
        MemberOf(Home, role, members);

        // Act
        await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyABabyLeftAfterTheMove_DeletesTheOldFamilyWithIt()
    {
        // Arrange: no entries are left, only the baby: the old family goes with it (owner, 2026-11-03: the joined family has
        // the baby, so keeping it showed one baby twice).
        await SignInAsync();
        MovableAdoptedFamily();
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]), new LocalSummary(0, [new LocalBaby(MyLena, "Lena")]));

        // Act
        await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await _api.Received(1).LeaveAsync(Home, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SignedOutAndInBeforeTheWrite_WritesNothingAndThrows()
    {
        // Arrange
        await SignInAsync();
        MovableAdoptedFamily();
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            await SignInAsync();
            return new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]);
        });

        // Act
        Func<Task> act = async () => await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_SignedOut_Throws()
    {
        // Act
        Func<Task> act = async () => await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_MoveRefusedBecauseTheSourceSyncedMeanwhile_CopiesInstead()
    {
        // Arrange: the port re-checks inside its transaction; a family that ever synced is never re-keyed (R5).
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(false);
        MemberOf(Home, Role.Owner, 1);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Copied);
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_SyncedSourceOwnedAlone_CopiesWithAReservedStampRangeAndAsksToDeleteIt()
    {
        // Arrange: spec §6.2 and ruling 8.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, Role.Owner, 1);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyLena] = TheirLena }, CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.Copied, 40, 2, OldFamilyQuestion.DeleteOld));
        Received.InOrder(() =>
        {
            _records.CountRecordsAsync(Home, Arg.Any<CancellationToken>());
            _clock.ObserveAsync(new HlcStamp(1_790_000_000_000, 49, Phone), Arg.Any<CancellationToken>());
            _records.CopyAsync(Arg.Is<BringOverPlan>(p => p.From == Home && p.To == Joined), Me, new HlcStamp(1_790_000_000_000, 4, Phone), 45, Arg.Any<CancellationToken>());
        });
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_ReservedRangeTooSmallOnce_CountsReservesAgainAndRetries()
    {
        // Arrange: a pull added records between the count and the copy (Task 12's budget check, nothing written).
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, Role.Owner, 1);
        _records.CountRecordsAsync(Home, Arg.Any<CancellationToken>()).Returns(45, 47);
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 4, Phone), new HlcStamp(1_790_000_000_000, 50, Phone));
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(
            _ => throw new StampBudgetExceededException(), _ => new CopyResult(42, 0));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.Copied, 42, 0, OldFamilyQuestion.DeleteOld));
        Received.InOrder(() =>
        {
            _clock.ObserveAsync(new HlcStamp(1_790_000_000_000, 49, Phone), Arg.Any<CancellationToken>());
            _records.CopyAsync(Arg.Any<BringOverPlan>(), Me, new HlcStamp(1_790_000_000_000, 4, Phone), 45, Arg.Any<CancellationToken>());
            _clock.ObserveAsync(new HlcStamp(1_790_000_000_000, 97, Phone), Arg.Any<CancellationToken>());
            _records.CopyAsync(Arg.Any<BringOverPlan>(), Me, new HlcStamp(1_790_000_000_000, 50, Phone), 47, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_ReservedRangeTooSmallTwice_FailsAfterOneRetry()
    {
        // Arrange
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _records.CopyAsync(default!, default, default, default, default).ThrowsAsyncForAnyArgs(new StampBudgetExceededException());

        // Act
        Func<Task> act = async () => await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<StampBudgetExceededException>();
        await _records.ReceivedWithAnyArgs(2).CopyAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_CopyFailsForAnotherReason_SurfacesWithoutARetry()
    {
        // Arrange: only the stamp budget is retried (final review minor).
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _records.CopyAsync(default!, default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("disk"));

        // Act
        Func<Task> act = async () => await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _records.ReceivedWithAnyArgs(1).CopyAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhileTheCyclePushesTheNeverSyncedSource_DecidesAfterTheCycleAndCopies()
    {
        // Arrange: final review C1. The source's first push is unanswered: deciding now would move records the server is
        // about to hold under the source. The engine holds the work until the cycle has ended; by then the source has synced.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(true);
        MemberOf(Home, Role.Owner, 1);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(42, 0));
        var cycleEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sync.Configure().RunExclusiveAsync(default!, default).ReturnsForAnyArgs(async c =>
        {
            await cycleEnded.Task;
            await c.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
        });

        // Act
        var bring = UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);
        var movedDuringTheCycle = _moves.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.BringOverAsync));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        cycleEnded.SetResult();
        var outcome = await bring.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        movedDuringTheCycle.Should().BeFalse();
        outcome.Path.Should().Be(BringPath.Copied);
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Theory]
    [InlineData(Role.Parent, 2, OldFamilyQuestion.LeaveOld)]
    [InlineData(Role.Caregiver, 3, OldFamilyQuestion.LeaveOld)]
    [InlineData(Role.Owner, 2, OldFamilyQuestion.None)]
    public async Task ExecuteAsync_SyncedSourceByRole_AsksTheRightQuestion(Role role, int members, OldFamilyQuestion expected)
    {
        // Arrange: spec §6.2's table; an Owner with others is asked nothing (§8.3).
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, role, members);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 0));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Question.Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCopied_ReportsNothingNew()
    {
        // Arrange: Review Focus 3, a second tap or a retry after a crash.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _store.WasCopiedAsync(Home, Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.AlreadyCopied);
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
        await _clock.DidNotReceiveWithAnyArgs().ObserveAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_CopiedByAnotherTapMeanwhile_ReportsNothingNew()
    {
        // Arrange: the marker written in the copy's own transaction makes the copy answer null.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, Role.Owner, 1);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs((CopyResult?)null);

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None));
    }

    [Fact]
    public async Task ExecuteAsync_MappingNamesABabyThatIsNotTheTargets_AddsItAsNew()
    {
        // Arrange
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(1, 0));

        // Act
        await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyLena] = new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b7")) },
            CancellationToken.None);

        // Assert
        await _records.Received(1).CopyAsync(Arg.Is<BringOverPlan>(p => p.Babies[MyLena] == null), Me, Arg.Any<HlcStamp>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveEmptyAdoptedAsync_SignedOut_DoesNothing()
    {
        // Arrange
        MemberOf(Home, Role.Owner, 1);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));

        // Act
        await UseCase().LeaveEmptyAdoptedAsync(Home, CancellationToken.None);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }
}
