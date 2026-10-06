using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §5.3, §7.2, §8.4 and §9: the cycle over every account family.</summary>
public sealed class MultiFamilyCycleTests : IDisposable
{
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a5"));
    private static readonly HouseholdId Third = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a6"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly IFamilyStore _families = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _households = Substitute.For<IHouseholdApi>();
    private readonly SyncStops _stops = new();
    private readonly FakeTimeProvider _time = new(Now);

    public MultiFamilyCycleTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _moves.HasEverSyncedAsync(default, default).ReturnsForAnyArgs(true);
        _moves.MoveAllAsync(default, default, default, default).ReturnsForAnyArgs(true);
        _families.ListAsync(default, default).ReturnsForAnyArgs((IReadOnlyList<FamilyMembership>?)null);

        // Every family, including one re-keyed to a fresh ID during the test, has nothing to push and nothing new to pull.
        _store.NextBatchAsync(default, default, default).ReturnsForAnyArgs([]);
        _store.GetCursorAsync(default, default).ReturnsForAnyArgs(new SyncCursor(0, null));
        _api.PullAsync(default, default, default, default).ReturnsForAnyArgs(new PulledPage([], 0, false, 0));
    }

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>()), _store, _moves, _state, _api,
        Substitute.For<IVersionClock>(), Device(), _time, _families, _households, _stops);

    private static IDeviceIdentity Device()
    {
        var device = Substitute.For<IDeviceIdentity>();
        device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        return device;
    }

    private Task SignInAsync(HouseholdId? home = null, bool joinPending = false, HouseholdId? moveFrom = null) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, home ?? Household, joinPending, PendingMoveFrom: moveFrom),
        CancellationToken.None);

    private void Memberships(params HouseholdId[] families) => _families.ListAsync(User, Arg.Any<CancellationToken>()).Returns(
        [.. families.Select((f, i) => new FamilyMembership(f, i == 0 ? Role.Owner : Role.Parent, Now.AddDays(i), "Andrzej", 2))]);

    private void NoFamilyAndAFreshShownFamily()
    {
        Memberships();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
    }

    [Fact]
    public async Task RunAsync_ThreeAccountFamilies_SyncsTheHomeFirstThenTheOthersInJoinedOrder()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Third, Joined);
        var order = new List<HouseholdId>();
        _api.PullAsync(default, default, default, default).ReturnsForAnyArgs(call =>
        {
            order.Add(call.Arg<HouseholdId>());
            return new PulledPage([], 0, false, 0);
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        order.Should().Equal(Household, Third, Joined);
        await _store.Received(1).PrepareForPushAsync(Joined, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OneFamilyRevoked_SoftLocksItAndStillSyncsTheOthers()
    {
        // Arrange: spec §7.2, a soft-locked family does not stop the others.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Joined));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _state.Received(1).SetSoftLockAsync(Joined, true, Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        report.Outcome.Should().Be(CycleOutcome.Synced, "the shown family is the home, which synced");
        report.RefreshFamilies.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ASoftLockedFamilyConfirmedRemoved_ReportsItsRemovalAfterSyncingTheOthers()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Removed && r.Household == Joined);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().PrepareForPushAsync(Joined, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_TheShownFamilyIsAnotherSoftLockedFamily_ReportsSoftLocked()
    {
        // Arrange: ruling 20, SoftLocked speaks for the shown family.
        await SignInAsync();
        Memberships(Household, Joined);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.SoftLocked && r.RefreshFamilies);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NotAMember_StopsThatFamilyUntilAWakeAndGoesOn()
    {
        // Arrange: spec §9, the family was deleted under this member.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Joined));
        var cycle = Cycle();

        // Act
        var first = await cycle.RunAsync(CancellationToken.None);
        var second = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        first.NotAMember.Should().Equal(Joined);
        first.RefreshFamilies.Should().BeTrue();
        _stops.IsStopped(Joined).Should().BeTrue();
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(2).PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        second.Outcome.Should().Be(CycleOutcome.Synced);
    }

    [Fact]
    public async Task RunAsync_NotAMemberNamingAnotherFamily_FailsAndStopsNothing()
    {
        // Arrange: Task 13, the refusal names the family it was for; another family's answer stops nobody.
        await SignInAsync();
        Memberships(Household, Joined);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Third));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        _stops.IsStopped(Joined).Should().BeFalse();
        _stops.IsStopped(Third).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_SignedOutBeforeTheNotAMemberAnswer_StopsNothing()
    {
        // Arrange: the answer belongs to an account this phone no longer holds (Spec 011 §8.1).
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns<Task<PulledPage>>(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            throw new SyncRefusedException(SyncRefusal.NotAMember, Household);
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Interrupted && r.NotAMember == null);
        _stops.IsStopped(Household).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_StoppedHome_IsSkippedAndTheOthersSync()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Joined);
        _stops.Stop(Household);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.DidNotReceive().PrepareForPushAsync(Household, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OneFamilyOffline_EndsTheCycleAsFailed()
    {
        // Arrange: spec §7.2, a failed family (no connection) ends the cycle and backs off.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _api.DidNotReceive().PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JoiningWithANeverSyncedSource_NeitherPushesNorPullsIt()
    {
        // Arrange: Review Focus 2 and ruling 5. The source is the adopted home family; the joined family is pulled.
        await SignInAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _moves.HasEverSyncedAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        Memberships(Household, Joined);
        _families.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Household, "Andrzej"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.DidNotReceive().PrepareForPushAsync(Household, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JoinedFamilyNotInTheListYet_IsPulledToo()
    {
        // Arrange: spec §5.3, the cycle pulls the joined family before the list was refreshed after the accept.
        await SignInAsync();
        Memberships(Household);
        _families.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Provisional, "Kasia"));
        _moves.HasEverSyncedAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_InvitationPendingAndANeverSyncedOtherFamily_NeitherPushesNorPullsIt()
    {
        // Arrange: ruling 5, mid-join only families that have synced go on.
        await SignInAsync();
        Memberships(Household, Third);
        _moves.HasEverSyncedAsync(Third, Arg.Any<CancellationToken>()).Returns(false);
        _families.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.DidNotReceive().PrepareForPushAsync(Third, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_InvitationPendingAndCreatedMarker_MovesNothing()
    {
        // Arrange: ruling 4, the invitation's choice replaces the created move.
        await SignInAsync(moveFrom: Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
        _families.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now));

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        _sessions.Stored!.PendingMoveFrom.Should().Be(Provisional, "the flow clears it when it ends");
    }

    [Fact]
    public async Task RunAsync_JoinPendingAndThePullIsRevoked_SoftLocksTheHome()
    {
        // Arrange: Spec 011 ADR 0002 §10 step 2 still holds for the join-pending pull; ruling 20, it is SoftLocked.
        await SignInAsync(joinPending: true);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Household));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        await _state.Received(1).SetSoftLockAsync(Household, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JoinPendingAndTheListLacksTheHome_KeepsTheHome()
    {
        // Arrange: Spec 011's join-pending logic applies to the home family unchanged (spec §7.1).
        await SignInAsync(joinPending: true);
        Memberships(Joined);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.JoinPending);
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_HomeNoLongerAMembership_TheEarliestAccountFamilyBecomesHome()
    {
        // Arrange: spec §7.1, the home family was left.
        await SignInAsync();
        Memberships(Joined, Third);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        _sessions.Stored!.AccountHousehold.Should().Be(Joined);
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SignedOutBeforeTheHomeMoves_MovesNothingAndSyncsNothing()
    {
        // Arrange: Spec 011 §8.1, the home moves only under the cycle's generation.
        await SignInAsync();
        _families.ListAsync(User, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return (IReadOnlyList<FamilyMembership>?)[new FamilyMembership(Joined, Role.Parent, Now, "Kasia", 2)];
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _state.DidNotReceiveWithAnyArgs().IsSoftLockedAsync(default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_CreatedMarkerAndTheHomeMoved_ClearsItAndMovesNothingIntoTheNewHome()
    {
        // Arrange: the `created` move names the family the sign-in created, never the home that replaced it.
        await SignInAsync(moveFrom: Provisional);
        Memberships(Joined);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        _sessions.Stored!.Should().Match<StoredSession>(s => s.AccountHousehold == Joined && s.PendingMoveFrom == null);
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoActiveFamilyAndAFreshShownFamily_CreatesItOnTheServerAndMakesItHome()
    {
        // Arrange: spec §8.4, after the removal of the only family.
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert: "the new family becomes the home family and syncs", in this cycle (a retried create answers 201 again).
        await _households.Received(1).CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>());
        _sessions.Stored!.AccountHousehold.Should().Be(Provisional);
        report.RefreshFamilies.Should().BeTrue();
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.Received(1).PrepareForPushAsync(Provisional, User, Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Provisional, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoActiveFamilyAndTheShownFamilySynced_CreatesNothingAndSyncsNothing()
    {
        // Arrange: spec §8.4 creates only a family that was never on a server.
        await SignInAsync();
        Memberships();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _households.DidNotReceiveWithAnyArgs().CreateHouseholdAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_NoActiveFamilyWhileAnInvitationIsPending_CreatesNothing()
    {
        // Arrange: spec §5.3, mid-join the joined family gives the account its family; nothing of this phone's moves first.
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _families.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now));

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _households.DidNotReceiveWithAnyArgs().CreateHouseholdAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
    }

    [Fact]
    public async Task RunAsync_SignedOutBeforeTheCreate_CreatesNothing()
    {
        // Arrange: a family created on the server under an account this phone no longer holds would be someone else's.
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return false;
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _households.DidNotReceiveWithAnyArgs().CreateHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_SignedOutWhileCreating_SetsNoHomeAndSyncsNothing()
    {
        // Arrange
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).Returns(async _ =>
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        _sessions.Stored.Should().BeNull();
        await _state.DidNotReceiveWithAnyArgs().IsSoftLockedAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
    }

    [Fact]
    public async Task RunAsync_CreatedIdTaken_ReKeysTheFamilyOnceAndCreatesItAgain()
    {
        // Arrange
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.IdTaken));
        HouseholdId? fresh = null;
        _moves.MoveAllAsync(Provisional, Arg.Do<HouseholdId>(id => fresh = id), User, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        fresh.Should().NotBeNull().And.NotBe(Provisional);
        await _households.Received(1).CreateHouseholdAsync(fresh!.Value, Arg.Any<CancellationToken>());
        _sessions.Stored!.AccountHousehold.Should().Be(fresh.Value);
        report.DataChanged.Should().BeTrue("the shown family's ID changed");
        await _api.Received(1).PullAsync(fresh.Value, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CreatedIdTakenTwice_FailsAfterOneReKey()
    {
        // Arrange
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(default, default).ThrowsAsyncForAnyArgs(new HouseholdRefusedException(HouseholdErrors.IdTaken));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _moves.ReceivedWithAnyArgs(1).MoveAllAsync(default, default, default, default);
        await _households.ReceivedWithAnyArgs(2).CreateHouseholdAsync(default, default);
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
    }

    [Fact]
    public async Task RunAsync_CreatedIdTakenButTheReKeyRefused_CreatesNothingMore()
    {
        // Arrange: MoveAllAsync never re-keys a family that has synced; the fresh ID then holds none of its records.
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.IdTaken));
        _moves.MoveAllAsync(default, default, default, default).ReturnsForAnyArgs(false);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _households.ReceivedWithAnyArgs(1).CreateHouseholdAsync(default, default);
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
    }

    [Fact]
    public async Task RunAsync_SignedOutBeforeTheReKey_MovesNothing()
    {
        // Arrange
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).Returns<Task>(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            throw new HouseholdRefusedException(HouseholdErrors.IdTaken);
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_CreatedButAlreadyHasFamily_RefreshesTheListAndKeepsTheFamilyLocal()
    {
        // Arrange: invited back meanwhile (spec §8.4).
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.AlreadyHasFamily));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.RefreshFamilies.Should().BeTrue();
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_CreateRefusedOtherwiseOrUnanswered_FailsAndSetsNoHome(bool refused)
    {
        // Arrange
        await SignInAsync();
        NoFamilyAndAFreshShownFamily();
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(
            refused ? new HouseholdRefusedException("auth.rate-limited") : new HouseholdUnavailableException("offline"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
    }

    [Fact]
    public async Task RunAsync_MalformedRefusal_StopsAsNoProgress()
    {
        // Arrange: spec §9.
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.Malformed, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Failed && r.Error == SyncCycle.NoProgressError);
    }

    [Fact]
    public async Task RunAsync_StoppedFamily_IsSkipped()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Joined);
        _stops.Stop(Joined);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }
}
