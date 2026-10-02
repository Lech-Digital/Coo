using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §4–§6 and §10 (Mobile.Application row). Only the outbound ports are substitutes.</summary>
public sealed class SyncCycleTests : IDisposable
{
    internal static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    internal static readonly AccountProfile Profile = new(User, "Andrzej", "andrzej@example.com");
    internal static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    internal static readonly HouseholdId Provisional = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    internal static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IConsentApi _consentApi = Substitute.For<IConsentApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly FakeTimeProvider _time = new(Now);

    public SyncCycleTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
    }

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, _consentApi), _store, _moves, _state, _api, _clock, _device, _time);

    private Task SignInAsync(bool joinPending = false) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, joinPending), CancellationToken.None);

    internal static OutgoingChange Change(int n) => new(
        Guid.Parse("7a1c0000-0000-7000-8000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), SyncEntityTypes.CareEntry,
        Guid.Parse("7a1c0000-0000-7000-9000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), false,
        new HlcStamp(1_790_000_000_000 + n, 0, Phone), n, null);

    [Fact]
    public async Task RunAsync_SignedOut_TouchesNothing()
    {
        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.NotSignedIn);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_SignedInWithoutConsent_NeitherPushesNorPulls()
    {
        // Arrange: upgraded from 0.10.0 while signed in (Review Focus 1), and the server has no consent either.
        await SignInAsync();
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Household, "owner", []));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentNeeded);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ConsentDeclined_NeitherPushesNorPulls()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentDeclined);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_JoinPending_PullsTheAccountsHouseholdAndPushesNothing()
    {
        // Arrange: this phone shows its own household; the account's is pulled beside it (ruling 8).
        await SignInAsync(joinPending: true);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.JoinPending);
        await _api.Received(1).PullAsync(Household, 0, null, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
    }

    [Fact]
    public async Task RunAsync_Created_MovesEverythingBeforeTheFirstPush()
    {
        // Arrange: the account's household is new and empty; this phone still shows its provisional one (ruling 9).
        await SignInAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        report.DataChanged.Should().BeTrue();
        Received.InOrder(() =>
        {
            _moves.MoveAllAsync(Provisional, Household, User, Arg.Any<CancellationToken>());
            _store.PrepareForPushAsync(Household, User, Arg.Any<CancellationToken>());
            _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunAsync_Changes_PushesThenAppliesTheResultsThenPulls()
    {
        // Arrange
        await SignInAsync();
        var batch = new[] { Change(1), Change(2) };
        var results = new[] { new PushResult(batch[0].ChangeId, PushOutcome.Applied, null), new PushResult(batch[1].ChangeId, PushOutcome.Rejected, "time.end-not-after-start") };
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch, []);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(results);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        Received.InOrder(() =>
        {
            _store.PrepareForPushAsync(Household, User, Arg.Any<CancellationToken>());
            _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>());
            _store.ApplyResultsAsync(Household, batch, results, Now, Arg.Any<CancellationToken>());
            _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunAsync_PushFailsOnTheWire_KeepsTheOutboxAndFails()
    {
        // Arrange: "nothing was decided" (ADR 0002 §5): no result is applied, so no outbox row goes.
        await SignInAsync();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(Task.FromException<IReadOnlyList<PushResult>>(new SyncUnavailableException("offline")));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.DidNotReceiveWithAnyArgs().ApplyResultsAsync(default, default!, default!, default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ServerAnsweredOnlySomeChanges_AppliesThoseAndFails()
    {
        // Arrange
        await SignInAsync();
        var batch = new[] { Change(1), Change(2) };
        var partial = new[] { new PushResult(batch[0].ChangeId, PushOutcome.Applied, null) };
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(partial);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.Received(1).ApplyResultsAsync(Household, batch, partial, Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SignedOutBetweenBatches_StopsBeforeTheNextBatch()
    {
        // Arrange: a full batch, then the person signs out while it is in flight (Review Focus 5).
        await SignInAsync();
        var batch = Enumerable.Range(1, SyncCycle.PushBatchSize).Select(Change).ToArray();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return (IReadOnlyList<PushResult>)[.. batch.Select(c => new PushResult(c.ChangeId, PushOutcome.Applied, null))];
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _store.Received(1).NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ABare403_FailsAndDeletesOrLocksNothing()
    {
        // Arrange: a proxy's 403 reaches the cycle as "unavailable" (ADR 0002 §10 step 1).
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("403"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_MembershipRevokedForThisHousehold_SoftLocksAndDeletesNothing()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Household));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        await _state.Received(1).SetSoftLockAsync(Household, true, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_MembershipRevokedNamingAnotherHousehold_IsAFailedCycle()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Provisional));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
    }

    [Theory]
    [InlineData(MembershipStatus.Revoked, CycleOutcome.Removed)]
    [InlineData(MembershipStatus.Unknown, CycleOutcome.SoftLocked)]
    public async Task RunAsync_SoftLocked_ConfirmsButNeverDeletes(MembershipStatus status, CycleOutcome expected)
    {
        // Arrange: deletion waits for the person (ruling 13).
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(status);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(expected);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_SoftLockedButActiveAgain_LiftsTheLock()
    {
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Active);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _state.Received(1).SetSoftLockAsync(Household, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ServerSaysConsentRequired_ForgetsTheCachedConsent()
    {
        // Arrange: a new wording on the server (Spec 011 §3).
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.ConsentRequired, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentNeeded);
        await _state.Received(1).SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ClientTooOld_ReportsItAndKeepsTheOutbox()
    {
        // Arrange
        await SignInAsync();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(
            Task.FromException<IReadOnlyList<PushResult>>(new SyncRefusedException(SyncRefusal.ClientTooOld, null)));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ClientTooOld);
        await _store.DidNotReceiveWithAnyArgs().ApplyResultsAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunAsync_PulledPages_AppliesEachAndObservesTheNewestStamp()
    {
        // Arrange
        await SignInAsync();
        var older = new HlcStamp(10, 0, Phone);
        var newest = new HlcStamp(20, 0, Phone);
        PulledRecord Record(HlcStamp v) => new(SyncEntityTypes.DuplicateDismissal, Guid.Parse("7a1c0000-0000-7000-9000-000000000001"), v, false,
            new DismissalPayload(Guid.Parse("7a1c0000-0000-7000-9000-000000000002"), Guid.Parse("7a1c0000-0000-7000-9000-000000000003")));
        var first = new PulledPage([Record(older)], 1, true, 2);
        var second = new PulledPage([Record(newest)], 2, false, 2);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null), new SyncCursor(1, 2));
        _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>()).Returns(first);
        _api.PullAsync(Household, 1, 2, Arg.Any<CancellationToken>()).Returns(second);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.DataChanged.Should().BeTrue();
        await _store.Received(1).ApplyPageAsync(Household, first, Arg.Any<CancellationToken>());
        await _store.Received(1).ApplyPageAsync(Household, second, Arg.Any<CancellationToken>());
        await _clock.Received(1).ObserveAsync(newest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PageSaysMoreWithoutMovingTheCursor_Fails()
    {
        // Arrange: a server bug must not spin the phone forever (ruling 16).
        await SignInAsync();
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(5, null));
        _api.PullAsync(Household, 5, null, Arg.Any<CancellationToken>()).Returns(new PulledPage([], 5, true, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.DidNotReceiveWithAnyArgs().ApplyPageAsync(default, default!, default);
    }

    [Fact]
    public async Task RunAsync_JoinPendingButTheBringOverAlreadyCommitted_CompletesTheJoin()
    {
        // Arrange: the app stopped between the bring-over's transaction and the session update.
        await SignInAsync(joinPending: true);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task RunAsync_BootstrapPageEmptyButHasMore_KeepsPulling()
    {
        // Arrange: a bootstrap page can carry no record (tombstones skipped) and still say more follow.
        await SignInAsync();
        var empty = new PulledPage([], 500, true, 900);
        var last = new PulledPage([], 900, false, 900);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null), new SyncCursor(500, 900));
        _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>()).Returns(empty);
        _api.PullAsync(Household, 500, 900, Arg.Any<CancellationToken>()).Returns(last);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.Received(1).ApplyPageAsync(Household, empty, Arg.Any<CancellationToken>());
        await _store.Received(1).ApplyPageAsync(Household, last, Arg.Any<CancellationToken>());
        await _clock.DidNotReceiveWithAnyArgs().ObserveAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_PhoneAlreadyHoldsRecords_PullsFromZeroWithHeadZero()
    {
        // Arrange: the store answers head 0 for a household this phone holds records of (ruling 20); the cycle sends it as is.
        await SignInAsync();
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, 0));

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.Received(1).PullAsync(Household, 0, 0, Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, 0, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SignedOutWhileAPageIsInFlight_DoesNotApplyIt()
    {
        // Arrange: the page belongs to an account this phone no longer holds (Spec 011 §8.1).
        await SignInAsync();
        var page = new PulledPage([], 3, true, null);
        _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return page;
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _store.DidNotReceiveWithAnyArgs().ApplyPageAsync(default, default!, default);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SoftLocked_NeitherPreparesNorPushes()
    {
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
    }

    [Fact]
    public async Task RunAsync_RevokedRefusalDuringPush_KeepsTheOutbox()
    {
        // Arrange: a refusal decides nothing about the changes; their outbox rows stay for after the soft-lock lifts.
        await SignInAsync();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(
            Task.FromException<IReadOnlyList<PushResult>>(new SyncRefusedException(SyncRefusal.MembershipRevoked, Household)));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        await _store.DidNotReceiveWithAnyArgs().ApplyResultsAsync(default, default!, default!, default, default);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }
}
