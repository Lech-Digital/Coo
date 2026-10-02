using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using NSubstitute;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §4: the choice, and the plan that one transaction carries out.</summary>
public sealed class BringEntriesOverTests : IDisposable
{
    private static readonly BabyId LocalBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId FamilyBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public BringEntriesOverTests()
    {
        _account = new Account(_sessions);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.SummaryAsync(Provisional, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new(LocalBaby, "Maja")]));
        _moves.SummaryAsync(Household, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new(FamilyBaby, "Maja")]));
    }

    public void Dispose() => _account.Dispose();

    private BringEntriesOver UseCase() => new(_sessions, _moves, _account, _sync);

    private Task JoinPendingAsync() => _account.SignedInAsync(
        new StoredSession("access", DateTimeOffset.UnixEpoch, "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

    [Fact]
    public async Task PrepareAsync_JoinPending_ReturnsThisPhonesEntriesAndTheFamilysBabies()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        var choice = await UseCase().PrepareAsync(CancellationToken.None);

        // Assert
        choice!.Local.EntryCount.Should().Be(42);
        choice.Family.Should().ContainSingle().Which.Id.Should().Be(FamilyBaby);
    }

    [Fact]
    public async Task ExecuteAsync_MappedToAFamilyBaby_CarriesThePlanAndCompletesTheJoin()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = FamilyBaby }, CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        await _moves.Received(1).BringOverAsync(
            Arg.Is<BringOverPlan>(p => p.From == Provisional && p.To == Household && p.Babies[LocalBaby] == FamilyBaby), User, Arg.Any<CancellationToken>());
        _account.State.Should().Be(new SignedIn(Profile));
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task ExecuteAsync_MappedToABabyOutsideTheFamily_AddsItAsNew()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000bb")) }, CancellationToken.None);

        // Assert
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.Babies[LocalBaby] == null), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NotJoinPending_DoesNothing()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("access", DateTimeOffset.UnixEpoch, "refresh", Profile, Household, JoinPending: false), CancellationToken.None);

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task PrepareAsync_ActiveHouseholdHasSynced_OffersNothingAndTheAccountsHouseholdBecomesActive()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var choice = await UseCase().PrepareAsync(CancellationToken.None);

        // Assert
        choice.Should().BeNull();
        await _moves.Received(1).ActivateAsync(Household, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        _account.State.Should().Be(new SignedIn(Profile));
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task ExecuteAsync_TheMoveRefusesASyncedSource_StillCompletesTheJoin()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(false);

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = FamilyBaby }, CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_SignedOutBeforeTheMove_MovesNothing()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.SummaryAsync(Provisional, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return new LocalSummary(42, [new(LocalBaby, "Maja")]);
        });

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = FamilyBaby }, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyInTheAccountsHousehold_OnlyCompletesTheJoin()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_ALocalBabyAbsentFromTheMapping_IsAddedAsNew()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.Babies.ContainsKey(LocalBaby) && p.Babies[LocalBaby] == null), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SignedOutDuringTheMove_ReportsFalseAndRequestsNoSync()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return true;
        });

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = FamilyBaby }, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        _sync.DidNotReceive().RequestSync();
    }

    /// <summary>Hands out the session it read, then signs the account out: the sign-out lands while the use case awaits the store.</summary>
    private sealed class SigningOutSessions(MemorySessions inner, Func<Task> signOut) : ISessionStore
    {
        public async Task<StoredSession?> GetAsync(CancellationToken cancellationToken)
        {
            var session = await inner.GetAsync(cancellationToken);
            await signOut();
            return session;
        }

        public Task SaveAsync(StoredSession session, CancellationToken cancellationToken) => inner.SaveAsync(session, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);
    }

    [Fact]
    public async Task PrepareAsync_SignedOutWhileReadingTheSession_ActivatesNothingAndDoesNotCompleteTheJoin()
    {
        // Arrange
        await JoinPendingAsync();
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(true);
        var useCase = new BringEntriesOver(
            new SigningOutSessions(_sessions, () => _account.SignedOutAsync(sessionExpired: false, CancellationToken.None)), _moves, _account, _sync);

        // Act
        var choice = await useCase.PrepareAsync(CancellationToken.None);

        // Assert
        choice.Should().BeNull();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        _account.State.Should().Be(new SignedOut(false));
        _sync.DidNotReceive().RequestSync();
    }
}
