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
    private readonly IHouseholdStore _onPhone = Substitute.For<IHouseholdStore>();
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
        OnPhone(Household);
    }

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>()), _store, _moves, _state, _api,
        Substitute.For<IVersionClock>(), Device(), _time, _families, _households, _stops, _onPhone);

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

    private void OnPhone(params HouseholdId[] families) =>
        _onPhone.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(FamiliesTests.Settings)]);

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
    public async Task RunAsync_JoinedFamilySyncedButNotInTheListYet_IsNotAskedAboutAsADroppedFamily()
    {
        // Arrange: C1. The list is refreshed after the accept; until then the joined family is synced, not missing.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined);
        _families.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Provisional, "Kasia"));

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().GetMembershipAsync(default, default);
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
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false, true);
        OnPhone(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert: the family it just created is not asked about as a dropped one.
        await _api.DidNotReceiveWithAnyArgs().GetMembershipAsync(default, default);

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

    [Fact]
    public async Task RunAsync_ASoftLockedFamilyTheRefreshDropped_ReportsItsRemoval()
    {
        // Arrange: C1. /api/me lists active memberships only, so the refresh after the soft-lock dropped Joined.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Removed && r.Household == Joined);
        await _api.DidNotReceive().PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().PrepareForPushAsync(Joined, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RemovedWhileThePhoneWasClosed_SyncsTheNewHomeThenReportsTheRemoval()
    {
        // Arrange: C1. Never soft-locked; the list fetched at start already lacks the old home.
        await SignInAsync();
        Memberships(Joined);
        OnPhone(Household, Joined);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert: removal confirmation never blocks syncing the account families.
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Removed && r.Household == Household);
        Received.InOrder(() =>
        {
            _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
            _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>());
        });
        await _store.DidNotReceive().PrepareForPushAsync(Household, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AnotherAccountsFamilyOnThePhone_IsNeverARemoval()
    {
        // Arrange: C1. Third synced under another account (`/membership` answers not-a-member, read as Unknown); Provisional is local.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Third, Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
        _api.GetMembershipAsync(Third, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Household == null && !r.RefreshFamilies);
        await _api.DidNotReceive().GetMembershipAsync(Provisional, Arg.Any<CancellationToken>());
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
        await _api.DidNotReceive().PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AFamilyMissingFromTheListIsActive_AsksForARefresh()
    {
        // Arrange: C1, the stored list is stale.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Active);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.RefreshFamilies);
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
        await _api.DidNotReceive().PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ListNotKnown_AsksAboutNoFamilyMissingFromIt()
    {
        // Arrange: C1, nothing can be missing from a list never fetched.
        await SignInAsync();
        OnPhone(Household, Joined);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _api.DidNotReceiveWithAnyArgs().GetMembershipAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_AFamilyWhoseSettingsAreDeleted_IsNotAskedAbout()
    {
        // Arrange: as the Family section (Families.ListAsync), a household with deleted settings is not a family on the phone.
        await SignInAsync();
        Memberships(Household);
        var deleted = FamiliesTests.Settings(Joined);
        deleted.Delete(new HlcStamp(1_790_000_000_001, 0, Phone));
        _onPhone.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household), deleted]);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().GetMembershipAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_TwoDroppedFamiliesRevoked_ReportsTheFirstAndAsksNoMore()
    {
        // Arrange: ruling 23, the removal sheet shows one removal at a time.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined, Third);
        _api.GetMembershipAsync(default, default).ReturnsForAnyArgs(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Household.Should().Be(Joined);
        await _api.DidNotReceive().GetMembershipAsync(Third, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SignedOutWhileAskingAboutADroppedFamily_ReportsNoRemoval()
    {
        // Arrange: Spec 011 §8.1.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return MembershipStatus.Revoked;
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Interrupted && r.Household == null);
    }

    [Fact]
    public async Task RunAsync_NotAMemberThenAFamilyOffline_FailsAndKeepsTheStopAndTheRefresh()
    {
        // Arrange: I2, a failed report still carries what the cycle found before it failed.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Joined));
        _api.PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Failed && r.RefreshFamilies);
        report.NotAMember.Should().Equal(Joined);
    }

    [Theory]
    [InlineData(SyncRefusal.ConsentRequired, CycleOutcome.ConsentNeeded)]
    [InlineData(SyncRefusal.ClientTooOld, CycleOutcome.ClientTooOld)]
    [InlineData(SyncRefusal.Malformed, CycleOutcome.Failed)]
    public async Task RunAsync_NotAMemberThenACycleWideRefusal_KeepsTheStopAndTheRefresh(SyncRefusal refusal, CycleOutcome expected)
    {
        // Arrange: I2.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Joined));
        _api.PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncRefusedException(refusal, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == expected && r.RefreshFamilies);
        report.NotAMember.Should().Equal(Joined);
    }

    [Fact]
    public async Task RunAsync_NotAMemberThenNoProgress_KeepsTheStopAndTheRefresh()
    {
        // Arrange: I2, an undecided push on a later family.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Joined));
        var batch = new[] { Change(1) };
        _store.NextBatchAsync(Third, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Third, Phone, batch, Arg.Any<CancellationToken>()).Returns([]);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Failed && r.Error == SyncCycle.NoProgressError && r.RefreshFamilies);
        report.NotAMember.Should().Equal(Joined);
    }

    [Fact]
    public async Task RunAsync_AccountFamilyActiveAndALocalNeverSyncedFamilyShown_CreatesNothing()
    {
        // Arrange: M4, §8.4 creates only when the account has no active family; a local family chosen in the switcher stays local.
        await SignInAsync();
        Memberships(Household);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _households.DidNotReceiveWithAnyArgs().CreateHouseholdAsync(default, default);
        await _store.DidNotReceive().PrepareForPushAsync(Provisional, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_HomeLeftAndALocalNeverSyncedFamilyShown_MovesTheHomeAndCreatesNothing()
    {
        // Arrange: M4, with another active family the home moves to it instead.
        await SignInAsync();
        Memberships(Joined);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _households.DidNotReceiveWithAnyArgs().CreateHouseholdAsync(default, default);
        _sessions.Stored!.AccountHousehold.Should().Be(Joined);
    }

    [Fact]
    public async Task RunAsync_OneFamilyRemovedAndTheShownOneSoftLocked_ReportsRemoved()
    {
        // Arrange: M5, ruling 20, Removed (any family) outranks SoftLocked (the shown family).
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        _state.IsSoftLockedAsync(Third, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Third, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Removed && r.Household == Third && r.RefreshFamilies);
    }

    [Fact]
    public async Task RunAsync_SoftLockLiftedOnAnotherFamily_StopsThereAndRunsAgainAtOnce()
    {
        // Arrange: M5. A lifted soft-lock ends this cycle; the next one, at once, syncs every family with the lock gone.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Active);
        OnPhone(Household, Joined, Third, Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert: nor is a dropped family asked about in the ended cycle.
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Interrupted && r.RunAgain);
        await _api.DidNotReceive().GetMembershipAsync(Provisional, Arg.Any<CancellationToken>());
        await _state.Received(1).SetSoftLockAsync(Joined, false, Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_TheShownAndOnlyFamilySoftLockedWithAnEmptyList_ReportsSoftLocked()
    {
        // Arrange: review I2, spec §9: the line says "Sharing is paused", not "Up to date".
        await SignInAsync();
        Memberships();
        OnPhone(Household);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        report.Household.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ADroppedFamilySoftLockedButNotShown_StaysSynced()
    {
        // Arrange: only the shown family speaks for the line (ruling 20).
        await SignInAsync();
        Memberships(Household);
        OnPhone(Household, Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
    }

    [Fact]
    public async Task RunAsync_TheShownFamilyDroppedButNotSoftLocked_StaysSynced()
    {
        // Arrange: another account's family shown (not-a-member, read as Unknown) is not paused.
        await SignInAsync();
        Memberships();
        OnPhone(Household);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
    }

    [Fact]
    public async Task RunAsync_TheHomeIsGoneFromThePhoneAndTheListUnknown_NeverSyncsIt()
    {
        // Arrange: review M-d, after an acknowledged removal with no list: a new provisional family is shown.
        await SignInAsync();
        OnPhone(Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().PrepareForPushAsync(Household, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _state.DidNotReceive().IsSoftLockedAsync(Household, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_TheHomeNotOnThePhoneButInTheList_SyncsIt()
    {
        // Arrange: an account family never pulled here (the home moved to it) arrives with its first pull.
        await SignInAsync();
        Memberships(Household);
        OnPhone(Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JoinPendingWithTheHomeNotOnThePhone_PullsIt()
    {
        // Arrange: the family's log arrives with the pull (Spec 011 ruling 8).
        await SignInAsync(joinPending: true);
        OnPhone(Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CreatedSignInWithTheHomeNotOnThePhone_MovesIntoItAndSyncsIt()
    {
        // Arrange: the `created` marker names the move into the account's new household (Spec 011 §4).
        await SignInAsync(moveFrom: Provisional);
        OnPhone(Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _moves.Received(1).MoveAllAsync(Provisional, Household, User, Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_TheHomesSettingsDeletedAndTheListUnknown_NeverSyncsIt()
    {
        // Arrange: a household whose settings are deleted is not on the phone (as the Family section reads it).
        await SignInAsync();
        var deleted = FamiliesTests.Settings(Household);
        deleted.Delete(new HlcStamp(1_790_000_000_001, 0, Phone));
        _onPhone.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([deleted, FamiliesTests.Settings(Provisional)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }
}
