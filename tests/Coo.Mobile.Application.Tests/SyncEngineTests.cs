using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
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
        var cycle = new SyncCycle(_account, _sessions, consent, _store, _moves, _state, _api, Substitute.For<IVersionClock>(), device, _time);
        _engine = new SyncEngine(cycle, _store, _moves, _sessions, _account, consent, _writes, _time, new SyncOptions(() => 1.0));
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
        _store.CountsAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCounts(0, 2, 0));
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
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        await _engine.SyncNowAsync(CancellationToken.None);
        var removalBefore = _engine.Status.Removal;
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);

        // Act
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);

        // Assert
        removalBefore.Should().Be(Household);
        await _moves.Received(1).DeleteHouseholdAsync(Household, Arg.Any<CancellationToken>());
        _engine.Status.Removal.Should().BeNull();
    }

    [Fact]
    public async Task FreshBootstrapAsync_OutboxNotEmpty_RefusesAndPullsNothing()
    {
        // Arrange
        await SignInAsync();
        _moves.FreshBootstrapAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        _api.ClearReceivedCalls();

        // Act
        var done = await _engine.FreshBootstrapAsync(CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task JoinCompleted_BringOverDone_TellsThePagesToRefresh()
    {
        // Arrange (subscribed first: the engine's own first cycle may complete the join at once)
        var raised = 0;
        _engine.DataChanged += (_, _) => Interlocked.Increment(ref raised);
        await _account.SignedInAsync(
            new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

        // Act
        await _account.TryCompleteJoinAsync(_account.Generation, CancellationToken.None);
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        raised.Should().BeGreaterThan(0);
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
        _store.CountsAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCounts(0, 3, 0));
        var seen = new List<int>();
        _engine.StatusChanged += (_, _) => throw new InvalidOperationException("a broken view model");
        _engine.StatusChanged += (_, _) =>
        {
            lock (seen)
                seen.Add(_engine.Status.Pending);
        };

        // Act: the local write's fire-and-forget pending refresh, then a whole cycle, each with a throwing handler.
        _writes.Notify();
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        lock (seen)
            seen.Should().Contain(3);
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
    public async Task Start_HouseholdMismatch_StatusCarriesItAndTheForegroundTicksDoNotRetry()
    {
        // Arrange: no `created` marker names this move (Task 11); retrying cannot fix it.
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        await SignInAsync();
        await _engine.WhenIdleAsync();
        _engine.Start();
        await _engine.WhenIdleAsync();
        var status = _engine.Status;
        var attempts = _moves.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.ActiveHouseholdAsync));

        // Act: past the longest backoff, a tick at a time.
        for (var i = 0; i < 11; i++)
        {
            _time.Advance(SyncScheduler.ForegroundInterval);
            await _engine.WhenIdleAsync();
        }

        var afterTicks = _moves.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.ActiveHouseholdAsync));
        await _engine.SyncNowAsync(CancellationToken.None);
        var afterSyncNow = _moves.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IHouseholdMoves.ActiveHouseholdAsync));

        // Assert
        status.Last.Should().Be(CycleOutcome.Failed);
        status.LastError.Should().Be(SyncCycle.HouseholdMismatchError);
        afterTicks.Should().Be(attempts);
        afterSyncNow.Should().Be(attempts + 1);
        _engine.Status.LastError.Should().Be(SyncCycle.HouseholdMismatchError);
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
}
