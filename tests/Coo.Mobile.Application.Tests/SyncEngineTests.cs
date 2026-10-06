using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Core;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §2, §6.3, §11: the status the sync line reads, the triggers it wires, removal only on acknowledgement.</summary>
public sealed class SyncEngineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly IFamilyStore _familyStore = Substitute.For<IFamilyStore>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IHouseholdApi _householdApi = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _householdStore = Substitute.For<IHouseholdStore>();
    private readonly SyncStops _stops = new();
    private readonly LocalWrites _writes = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SyncEngine _engine;

    public SyncEngineTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _store.CountsAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCounts(0, 0, 0));
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        var device = Substitute.For<IDeviceIdentity>();
        device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        var consent = new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>());
        _records.CountPendingAsync(default!, default).ReturnsForAnyArgs(0);
        _familyStore.ListAsync(default, default).ReturnsForAnyArgs((IReadOnlyList<FamilyMembership>?)null);
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household)]);
        _store.NextBatchAsync(Provisional, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Provisional, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _api.PullAsync(Provisional, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        var locale = Substitute.For<IDeviceLocale>();
        locale.TimeZoneId.Returns("Europe/Amsterdam");
        var provisionalClock = Substitute.For<IVersionClock>();
        provisionalClock.NextAsync(Arg.Any<CancellationToken>()).Returns(new Coo.Domain.Common.Versioning.HlcStamp(1_790_000_000_000, 0, Phone));
        var families = new Families(_account, _familyStore, _accounts, _householdApi, _sessions, _householdStore, _moves, _state, _records,
            new ProvisionalSettings(provisionalClock, locale, _time));
        var cycle = new SyncCycle(
            _account, _sessions, consent, _store, _moves, _state, _api, Substitute.For<IVersionClock>(), device, _time, _familyStore,
            _householdApi, _stops, _householdStore);
        _engine = new SyncEngine(cycle, _store, _moves, _sessions, _account, consent, _writes, _time, families, _records, _stops,
            new SyncOptions(() => 1.0));
    }

    public void Dispose()
    {
        _engine.Dispose();
        _account.Dispose();
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: false), CancellationToken.None);

    [Fact]
    public async Task SyncNowAsync_Succeeds_StatusSaysSyncedWithTheTimeAndNothingPending()
    {
        // Arrange
        await SignInAsync();

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Should().Be(SyncStatus.Initial with { Last = CycleOutcome.Synced, LastSyncedAt = _time.GetLocalNow() });
    }

    [Fact]
    public async Task SyncNowAsync_Offline_StatusSaysFailedWithThePendingCount()
    {
        // Arrange
        await SignInAsync();
        _records.CountPendingAsync(default!, default).ReturnsForAnyArgs(2);
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncUnavailableException("offline")));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.Failed);
        _engine.Status.Pending.Should().Be(2);
    }

    [Fact]
    public async Task LocalWrite_AfterTheDebounce_RunsACycle()
    {
        // Arrange
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pulled.TrySetResult();
            return Task.FromResult(new PulledPage([], 0, false, 0));
        });

        // Act
        _writes.Notify();
        var beforeTheDebounce = pulled.Task.IsCompleted;
        _time.Advance(SyncScheduler.Debounce);

        // Assert (the timeout only bounds a broken build; the cycle starts inside Advance)
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        beforeTheDebounce.Should().BeFalse();
    }

    [Fact]
    public async Task SignedIn_AfterASignOutWithAnOldDecline_AsksAgain()
    {
        // Arrange: "Not now" lasts until the next sign-in (ruling 10).
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _state.Received().SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_AfterAConfirmedRemoval_DeletesTheHouseholdThenAndOnlyThen()
    {
        // Arrange: the removed family is the only one on the phone (§8.4).
        await SignInAsync();
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household)]);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        await _engine.SyncNowAsync(CancellationToken.None);
        var removalBefore = _engine.Status.Removal;
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);

        // Act
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);

        // Assert
        removalBefore.Should().Be(Household);
        await _records.Received(1).DeleteAndReplaceAsync(Household, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>());
        _engine.Status.Removal.Should().BeNull();
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_AfterAConfirmedRemoval_RefreshesTheListBeforeThePagesHearThenRunsACycle()
    {
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        await _engine.SyncNowAsync(CancellationToken.None);
        await _engine.WhenIdleAsync();
        _accounts.ClearReceivedCalls();
        _api.ClearReceivedCalls();
        var refreshedBeforeDataChanged = new List<bool>();
        _engine.DataChanged += (_, _) =>
        {
            lock (refreshedBeforeDataChanged)
                refreshedBeforeDataChanged.Add(_accounts.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAccountApi.GetMeAsync)));
        };

        // Act
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);
        await _engine.WhenIdleAsync();

        // Assert
        lock (refreshedBeforeDataChanged)
            refreshedBeforeDataChanged.Should().NotBeEmpty().And.HaveElementAt(0, true);
        await _api.Received().GetMembershipAsync(Household, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FreshBootstrapAsync_OutboxNotEmpty_RefusesAndPullsNothing()
    {
        // Arrange
        await SignInAsync();
        _moves.FreshBootstrapAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        await _engine.WhenIdleAsync(); // the sign-in's own cycle pulls; it must finish before the calls are cleared
        _api.ClearReceivedCalls();

        // Act
        var done = await _engine.FreshBootstrapAsync(CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task FreshBootstrapAsync_ACycleIsInFlight_WipesOnlyAfterTheCycleFinishes()
    {
        // Arrange: a cycle is held inside its pull.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        var inPull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            inPull.TrySetResult();
            await releasePull.Task;
            return new PulledPage([], 0, false, 0);
        });
        _moves.FreshBootstrapAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        var running = _engine.SyncNowAsync(CancellationToken.None);
        await inPull.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var bootstrap = _engine.FreshBootstrapAsync(CancellationToken.None);
        var wipedWhileRunning = _moves.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.FreshBootstrapAsync));
        releasePull.SetResult();
        var done = await bootstrap.WaitAsync(TimeSpan.FromSeconds(10));
        await running;

        // Assert
        wipedWhileRunning.Should().BeFalse();
        done.Should().BeTrue();
        await _moves.Received(1).FreshBootstrapAsync(Household, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinCompleted_BringOverDone_TellsThePagesToRefresh()
    {
        // Arrange (subscribed first: the engine's own first cycle may complete the join at once)
        var raised = new List<object?>();
        _engine.DataChanged += (sender, _) =>
        {
            lock (raised)
                raised.Add(sender);
        };
        await _account.SignedInAsync(
            new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

        // Act
        await _account.TryCompleteJoinAsync(_account.Generation, CancellationToken.None);
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert: once, for the join-pending to signed-in move (the pulls were empty, so no cycle reported a change).
        _account.State.Should().BeOfType<SignedIn>();
        lock (raised)
            raised.Should().ContainSingle().Which.Should().BeSameAs(_engine);
    }

    [Fact]
    public void NotifyDataChanged_Called_RaisesDataChangedOnce()
    {
        // Arrange
        var raised = 0;
        _engine.DataChanged += (_, _) => raised++;

        // Act
        _engine.NotifyDataChanged();

        // Assert
        raised.Should().Be(1);
    }

    [Fact]
    public async Task SignedOut_StatusSaysNotSignedIn()
    {
        // Arrange
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.NotSignedIn);
    }

    [Fact]
    public async Task SyncNowAsync_StoreThrows_StatusSaysFailedAndTheEngineBacksOffThenRunsAgain()
    {
        // Arrange: a store error propagates out of the cycle; the engine must turn it into a failed cycle.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<OutgoingChange>>(new InvalidOperationException("disk")));
        await _engine.SyncNowAsync(CancellationToken.None);
        var failed = _engine.Status;
        var attempts = _store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISyncStore.NextBatchAsync));

        // Act: a local write during the backoff waits for it.
        _writes.Notify();
        _time.Advance(SyncScheduler.Debounce);
        await _engine.WhenIdleAsync();
        var duringBackoff = _store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISyncStore.NextBatchAsync));
        _time.Advance(SyncScheduler.FirstBackoff);
        await _engine.WhenIdleAsync();
        var afterBackoff = _store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISyncStore.NextBatchAsync));

        // Assert
        failed.Last.Should().Be(CycleOutcome.Failed);
        failed.LastError.Should().Be(nameof(InvalidOperationException));
        failed.IsRunning.Should().BeFalse();
        duringBackoff.Should().Be(attempts);
        afterBackoff.Should().Be(attempts + 1);
    }

    [Fact]
    public async Task StatusChanged_AHandlerThrows_TheOtherHandlersStillRunAndTheEngineKeepsSyncing()
    {
        // Arrange
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);
        _records.CountPendingAsync(default!, default).ReturnsForAnyArgs(3);
        var batches = _store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISyncStore.NextBatchAsync));
        var pendingSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.StatusChanged += (_, _) => throw new InvalidOperationException("a broken view model");
        _engine.StatusChanged += (_, _) =>
        {
            if (_engine.Status.Pending == 3)
                pendingSeen.TrySetResult();
        };

        // Act: a local write, with no cycle yet (the debounce has not passed): only the fire-and-forget pending refresh runs.
        _writes.Notify();

        // Assert (the timeout only bounds a broken build)
        await pendingSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISyncStore.NextBatchAsync)).Should().Be(batches);
        await _engine.SyncNowAsync(CancellationToken.None);
        _engine.Status.Last.Should().Be(CycleOutcome.Synced);
        _engine.Status.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_StatusChangedHandlerThrows_DoesNotThrowAndTheFirstCycleRuns()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _engine.StatusChanged += (_, _) => throw new InvalidOperationException("a broken view model");
        _time.Advance(TimeSpan.FromMinutes(1));

        // Act
        var act = () => _engine.Start();

        // Assert
        act.Should().NotThrow();
        await _engine.WhenIdleAsync();
        _engine.Status.Should().Be(SyncStatus.Initial with { Last = CycleOutcome.Synced, LastSyncedAt = _time.GetLocalNow() });
    }

    [Fact]
    public async Task SyncNowAsync_NoProgress_StatusCarriesItAndLocalWritesDoNotRetryUntilTheAccountChanges()
    {
        // Arrange: the store hands out the same full batch after it was answered.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        var batch = Enumerable.Range(1, SyncCycle.PushBatchSize).Select(Change).ToList();
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Household, Phone, Arg.Any<IReadOnlyList<OutgoingChange>>(), Arg.Any<CancellationToken>())
            .Returns(batch.Select(c => new PushResult(c.ChangeId, PushOutcome.Applied, null)).ToList());
        await _engine.SyncNowAsync(CancellationToken.None);
        var status = _engine.Status;
        _api.ClearReceivedCalls();

        // Act
        _writes.Notify();
        _time.Advance(SyncScheduler.Debounce);
        await _engine.WhenIdleAsync();
        _time.Advance(SyncScheduler.MaxBackoff);
        await _engine.WhenIdleAsync();
        var pushesWhileStopped = _api.ReceivedCalls().Count();
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync();
        await _engine.WhenIdleAsync();

        // Assert
        status.Last.Should().Be(CycleOutcome.Failed);
        status.LastError.Should().Be("no-progress");
        pushesWhileStopped.Should().Be(0);
        await _api.ReceivedWithAnyArgs().PushAsync(default, default, default!, default);
    }

    [Fact]
    public async Task SignedOut_DuringARemovedCycle_TheLateResultDoesNotReachTheStatus()
    {
        // Arrange: the cycle has its Removed answer and is reading the pending count when the person signs out.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        var (entered, release) = HoldTheNextCount();
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        await entered.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        release.SetResult(0);
        await sync;
        await _engine.WhenIdleAsync();

        // Assert: nothing of the old account's removal is left for the next one to acknowledge.
        _engine.Status.Should().Be(SyncStatus.Initial with { Last = CycleOutcome.NotSignedIn });
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task SignedOut_DuringASyncedCycle_TheOldAccountsSyncTimeIsNotShown()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var (entered, release) = HoldTheNextCount();
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        await entered.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        release.SetResult(0);
        await sync;
        await _engine.WhenIdleAsync();

        // Assert
        _engine.Status.Should().Be(SyncStatus.Initial with { Last = CycleOutcome.NotSignedIn });
    }

    private (Task Entered, TaskCompletionSource<int> Release) HoldTheNextCount()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _records.CountPendingAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        return (entered.Task, release);
    }
    [Fact]
    public async Task SyncNowAsync_SoftLockLiftedByAnActiveMembership_RunsTheNextCycleWithoutWaitingForTheTick()
    {
        // Arrange (final review M5): locked for the first cycle only.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _api.ClearReceivedCalls();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true, false);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Active);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert: the follow-up cycle pulled; no time passed.
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        _engine.Status.Last.Should().Be(CycleOutcome.Synced);
    }

    [Theory]
    [InlineData(ConsentState.Given, true, true)]
    [InlineData(ConsentState.Given, false, false)]
    [InlineData(ConsentState.Declined, true, false)]
    public async Task SyncNowAsync_JoinPending_StatusSaysWhetherBringOverMayBeOffered(ConsentState consent, bool pulled, bool expected)
    {
        // Arrange (final review C1 b)
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(consent);
        _store.HasPulledAsync(Household, Arg.Any<CancellationToken>()).Returns(pulled);
        await _account.SignedInAsync(
            new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.JoinReady.Should().Be(expected);
    }

    [Fact]
    public async Task SyncNowAsync_PendingInTwoFamilies_StatusCountsBoth()
    {
        // Arrange: spec §7.2, Pending across the families that sync.
        await SignInAsync();
        _familyStore.ListAsync(User, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Household, Coo.Domain.Households.Role.Owner, Now, "Andrzej", 1),
            new FamilyMembership(Provisional, Coo.Domain.Households.Role.Parent, Now, "Andrzej", 2)]);
        _records.CountPendingAsync(
                Arg.Is<IReadOnlyCollection<HouseholdId>>(s => s.Count == 2 && s.Contains(Household) && s.Contains(Provisional)), Arg.Any<CancellationToken>())
            .Returns(3);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Pending.Should().Be(3);
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilyNotAMember_StatusSaysShownStoppedUntilTheNextWake()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncRefusedException(SyncRefusal.NotAMember, Household)));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);
        var stopped = _engine.Status.ShownStopped;
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        _engine.Resumed();
        await _engine.WhenIdleAsync();

        // Assert
        stopped.Should().BeTrue();
        _engine.Status.ShownStopped.Should().BeFalse("a resume is a wake");
    }

    [Fact]
    public async Task SyncNowAsync_AnotherFamilyNotAMember_StatusDoesNotSayShownStopped()
    {
        // Arrange: the stop belongs to a family that is not shown.
        await SignInAsync();
        _familyStore.ListAsync(User, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Household, Coo.Domain.Households.Role.Owner, Now, "Andrzej", 1),
            new FamilyMembership(Provisional, Coo.Domain.Households.Role.Parent, Now, "Andrzej", 2)]);
        _api.PullAsync(Provisional, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncRefusedException(SyncRefusal.NotAMember, Provisional)));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _stops.IsStopped(Provisional).Should().BeTrue();
        _engine.Status.ShownStopped.Should().BeFalse();
    }

    [Fact]
    public async Task SyncNowAsync_ACycleAsksForARefresh_RefreshesTheFamilyListAfterIt()
    {
        // Arrange: the wake's own refresh happens before the cycle; the cycle's not-a-member asks for another one.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncRefusedException(SyncRefusal.NotAMember, Household)));
        _accounts.ClearReceivedCalls();

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _accounts.Received(2).GetMeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNowAsync_NothingAsksForARefresh_RefreshesTheFamilyListOnlyAtTheWake()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _accounts.ClearReceivedCalls();
        _api.ClearReceivedCalls();

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);
        _writes.Notify();
        _time.Advance(SyncScheduler.Debounce);
        await _engine.WhenIdleAsync();

        // Assert: a local write is not a wake.
        await _accounts.Received(1).GetMeAsync(Arg.Any<CancellationToken>());
        await _api.Received(2).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNowAsync_TheRefreshThrows_TheCycleStillRuns()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<AccountOverview>(new InvalidOperationException("disk")));
        _api.ClearReceivedCalls();

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        _engine.Status.Last.Should().Be(CycleOutcome.Synced);
    }

    [Fact]
    public async Task Resumed_AWake_RefreshesTheFamilyListBeforeTheCycle()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Household, "owner", [], null,
            [new MembershipInfo(Household, Coo.Domain.Households.Role.Owner, Now)]));
        _householdApi.ListMembersAsync(Household, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], []));
        var order = new List<string>();
        _familyStore.When(f => f.ReplaceAsync(User, Arg.Any<IReadOnlyList<FamilyMembership>>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("refresh"));
        _api.When(a => a.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("pull"));

        // Act
        _engine.Resumed();
        await _engine.WhenIdleAsync();

        // Assert
        order.Should().StartWith(["refresh", "pull"]);
    }

    [Fact]
    public async Task Resumed_AFamilyStoppedAsNotAMember_SyncsItAgain()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _stops.Stop(Household);
        _api.ClearReceivedCalls();

        // Act
        _engine.Resumed();
        await _engine.WhenIdleAsync();

        // Assert
        _stops.IsStopped(Household).Should().BeFalse();
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignedOut_ClearsTheFamilyList()
    {
        // Arrange
        await SignInAsync();

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await _engine.WhenIdleAsync();

        // Assert
        await _familyStore.Received().ClearAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilySoftLockedWithAnother_SwitchesAndTellsThePages()
    {
        // Arrange: spec §8.2, a soft-locked family is hidden whenever another exists.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _api.ClearReceivedCalls();
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household), FamiliesTests.Settings(Provisional)]);
        _householdStore.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        _moves.When(m => m.ActivateAsync(Provisional, Arg.Any<CancellationToken>()))
            .Do(_ => _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional));
        var changed = 0;
        _engine.DataChanged += (_, _) => Interlocked.Increment(ref changed);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert: review M-b, the line speaks for the family now shown, not the hidden one.
        await _moves.Received(1).ActivateAsync(Provisional, Arg.Any<CancellationToken>());
        changed.Should().BeGreaterThan(0);
        _engine.Status.Last.Should().Be(CycleOutcome.Synced);
        _engine.Status.IsRunning.Should().BeFalse();
        await _api.Received(2).GetMembershipAsync(Household, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilySoftLockedAlone_NeitherSwitchesNorTellsThePages()
    {
        // Arrange: §8.2, the only family stays shown (Today's banner).
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household)]);
        _householdStore.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        var changed = 0;
        _engine.DataChanged += (_, _) => Interlocked.Increment(ref changed);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
        changed.Should().Be(0);
        _engine.Status.Last.Should().Be(CycleOutcome.SoftLocked);
    }

    [Fact]
    public async Task SyncNowAsync_HidingTheSoftLockedFamilyThrows_TheStatusStillSettles()
    {
        // Arrange
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        _householdStore.ListBabiesAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<IReadOnlyList<BabyProfile>>(new InvalidOperationException("disk")));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.SoftLocked);
        _engine.Status.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SyncNowAsync_TheShownFamilyCannotBeRead_TheStatusStillSettles()
    {
        // Arrange: the store fails for the cycle and for every read after it.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<HouseholdId>(new InvalidOperationException("disk")));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.Failed);
        _engine.Status.ShownStopped.Should().BeFalse();
        _engine.Status.IsRunning.Should().BeFalse();
    }

    /// <summary>A first cycle confirms <see cref="SyncCycleTests.Household"/>'s removal: the removal sheet is up.</summary>
    private async Task RemovedAsync()
    {
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        await _engine.SyncNowAsync(CancellationToken.None);
        await _engine.WhenIdleAsync();
        _engine.Status.Removal.Should().Be(Household);
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_ACycleInFlightFindsTheRemovalAgain_TheSheetDoesNotReopen()
    {
        // Arrange: review I1, a cycle that read the family before the acknowledgment deleted it.
        await RemovedAsync();
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<MembershipStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            asking.TrySetResult();
            return answer.Task;
        });
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        await asking.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: OK is pressed while the cycle is held; the family goes only once the cycle has heard "revoked" (final review C1).
        var acknowledge = _engine.AcknowledgeRemovalAsync(CancellationToken.None);
        var removedDuringTheCycle = _records.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IFamilyRecords.DeleteAndReplaceAsync));
        answer.SetResult(MembershipStatus.Revoked);
        await sync;
        await acknowledge.WaitAsync(TimeSpan.FromSeconds(10));
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Provisional)]);
        await _engine.WhenIdleAsync();

        // Assert
        removedDuringTheCycle.Should().BeFalse();
        _engine.Status.Removal.Should().BeNull();
        await _records.Received(1).DeleteAndReplaceAsync(Household, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunExclusiveAsync_WhileAPushIsUnanswered_RunsTheWorkOnlyAfterTheCycle()
    {
        // Arrange: final review C1, a move must not run while the cycle's first push of its source is unanswered.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        var change = new OutgoingChange(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f2"), "CareEntry", Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1"),
            false, new Coo.Domain.Common.Versioning.HlcStamp(1_790_000_000_000, 0, Phone), 1, null);
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([change], []);
        var pushing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<IReadOnlyList<PushResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.PushAsync(Household, Phone, Arg.Any<IReadOnlyList<OutgoingChange>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pushing.TrySetResult();
            return answer.Task;
        });
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        await pushing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resultsAppliedBeforeTheWork = false;

        // Act
        var exclusive = _engine.RunExclusiveAsync(
            _ =>
            {
                resultsAppliedBeforeTheWork = _store.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISyncStore.ApplyResultsAsync));
                return Task.CompletedTask;
            },
            CancellationToken.None);
        var ranDuringThePush = exclusive.IsCompleted;
        answer.SetResult([new PushResult(change.ChangeId, PushOutcome.Applied, null)]);
        await sync;
        await exclusive.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        ranDuringThePush.Should().BeFalse();
        resultsAppliedBeforeTheWork.Should().BeTrue();
    }

    [Fact]
    public async Task SyncNowAsync_JoinPendingWhileAJoinWaitsAtItsChoice_StatusSaysBringOverMayNotBeOffered()
    {
        // Arrange: final review C4, Spec 011's bring-over must not compete with the invitation's choice.
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _store.HasPulledAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _familyStore.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Household, Provisional, "Kasia"));
        await _account.SignedInAsync(
            new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.JoinReady.Should().BeFalse();
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_CalledTwiceAtOnce_RemovesTheFamilyOnce()
    {
        // Arrange: review I1, a double tap on OK must not leave two provisional families.
        await RemovedAsync();
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _records.DeleteAndReplaceAsync(Household, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            deleting.TrySetResult();
            return release.Task;
        });
        var first = _engine.AcknowledgeRemovalAsync(CancellationToken.None);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await first;
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);

        // Assert
        await _records.Received(1).DeleteAndReplaceAsync(Household, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_AnotherRemovalReportedMeanwhile_KeepsThatOne()
    {
        // Arrange: review I1 (c). Two dropped families on the phone, the list empty; the first is confirmed removed.
        _moves.HasEverSyncedAsync(default, default).ReturnsForAnyArgs(true);
        _familyStore.ListAsync(User, Arg.Any<CancellationToken>()).Returns([]);
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household), FamiliesTests.Settings(Provisional)]);
        _householdStore.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        _api.GetMembershipAsync(Provisional, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);
        await _engine.WhenIdleAsync();
        var first = _engine.Status.Removal;
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _moves.DeleteHouseholdAsync(Household, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            deleting.TrySetResult();
            return release.Task;
        });
        var acknowledge = _engine.AcknowledgeRemovalAsync(CancellationToken.None);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: while the first is being deleted, a cycle is asked for; it runs after the deletion and confirms the second.
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        _api.GetMembershipAsync(Provisional, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        var syncedDuringTheDeletion = sync.IsCompleted;
        release.SetResult();
        await acknowledge;
        await sync;
        await _engine.WhenIdleAsync();

        // Assert
        first.Should().Be(Household);
        syncedDuringTheDeletion.Should().BeFalse();
        _engine.Status.Removal.Should().Be(Provisional);
    }

    [Fact]
    public async Task SyncNowAsync_NotAMemberFromACycleThatBeganBeforeAWake_DoesNotOutliveTheWake()
    {
        // Arrange: review M-a, the stop lands after the wake cleared the stops.
        await SignInAsync();
        await _engine.WhenIdleAsync();
        var pulling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refuse = new TaskCompletionSource<PulledPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(new PulledPage([], 0, false, 0));
            pulling.TrySetResult();
            return refuse.Task;
        });
        var sync = _engine.SyncNowAsync(CancellationToken.None);
        await pulling.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        _engine.RequestSync();
        refuse.SetException(new SyncRefusedException(SyncRefusal.NotAMember, Household));
        await sync;
        await _engine.WhenIdleAsync();

        // Assert
        _stops.IsStopped(Household).Should().BeFalse();
        _engine.Status.ShownStopped.Should().BeFalse();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilyRemovedWithAnother_SwitchesAndStillOpensTheSheet()
    {
        // Arrange: only a paused line is handed to the next cycle; a removal is reported at once (ruling 23).
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household), FamiliesTests.Settings(Provisional)]);
        _householdStore.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act (the timeout only bounds a broken build: handing a removal to the next cycle would repeat it forever here)
        await _engine.SyncNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await _moves.Received(1).ActivateAsync(Provisional, Arg.Any<CancellationToken>());
        _engine.Status.Removal.Should().Be(Household);
        _engine.Status.Last.Should().Be(CycleOutcome.Removed);
    }
}
