using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §8.1: a refresh started before a sign-out or sign-in may not write afterwards.</summary>
public sealed class AccountGenerationTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);
    private static readonly StoredSession Session = new("access-1", Expires, "refresh-1", Profile, Household, JoinPending: false);
    private static readonly SessionTokens Fresh = new("access-2", Expires.AddMinutes(15), "refresh-2");

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;

    public AccountGenerationTests() => _account = new Account(_sessions);

    public void Dispose() => _account.Dispose();

    [Fact]
    public async Task SignedInAsync_ThenSignedOutAsync_MovesTheGenerationEachTime()
    {
        // Arrange
        var start = _account.Generation;

        // Act
        await _account.SignedInAsync(Session, CancellationToken.None);
        var afterSignIn = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Assert
        afterSignIn.Should().BeGreaterThan(start);
        _account.Generation.Should().BeGreaterThan(afterSignIn);
    }

    [Fact]
    public async Task TryReplaceTokensAsync_AfterASignOut_DoesNotSignBackIn()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeFalse();
        _sessions.Stored.Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task TryReplaceTokensAsync_AfterSigningInAgain_KeepsTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9" };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeFalse();
        _sessions.Stored.Should().Be(again);
    }

    [Fact]
    public async Task TryReplaceTokensAsync_SameGeneration_SavesTheTokensAndClearsThePendingOne()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.TrySetPendingRefreshTokenAsync(generation, "pending", CancellationToken.None);
        _sessions.Stored!.PendingRefreshToken.Should().Be("pending");

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeTrue();
        _sessions.Stored.Should().Be(Session with { AccessToken = "access-2", AccessTokenExpiresAt = Fresh.AccessTokenExpiresAt, RefreshToken = "refresh-2" });
        _account.Generation.Should().Be(generation, "replacing tokens is not a sign-in");
    }

    [Fact]
    public async Task TryExpireAsync_AfterADeliberateSignOut_DoesNotSayTheSessionExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        var expired = await _account.TryExpireAsync(generation, CancellationToken.None);

        // Assert
        expired.Should().BeFalse();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task TryExpireAsync_SameGeneration_SignsOutAsExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);

        // Act
        var expired = await _account.TryExpireAsync(_account.Generation, CancellationToken.None);

        // Assert
        expired.Should().BeTrue();
        _account.State.Should().Be(new SignedOut(SessionExpired: true));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfileAsync_NewName_KeepsTheGeneration()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;

        // Act
        await _account.UpdateProfileAsync(Profile with { Name = "Andrzej L." }, CancellationToken.None);

        // Assert
        _account.Generation.Should().Be(generation);
        _account.State.Should().Be(new SignedIn(Profile with { Name = "Andrzej L." }));
    }

    [Fact]
    public async Task TryCompleteJoinAsync_SameGeneration_SignsInFully()
    {
        // Arrange
        await _account.SignedInAsync(Session with { JoinPending = true }, CancellationToken.None);

        // Act
        var completed = await _account.TryCompleteJoinAsync(_account.Generation, CancellationToken.None);

        // Assert
        completed.Should().BeTrue();
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.JoinPending.Should().BeFalse();
    }

    [Fact]
    public async Task TrySetPendingRefreshTokenAsync_AfterSigningInAgain_LeavesTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9" };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        var set = await _account.TrySetPendingRefreshTokenAsync(generation, "pending", CancellationToken.None);

        // Assert
        set.Should().BeFalse();
        _sessions.Stored.Should().Be(again);
    }

    [Fact]
    public async Task TryCompleteJoinAsync_AfterSigningInAgain_LeavesTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session with { JoinPending = true }, CancellationToken.None);
        var generation = _account.Generation;
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9", JoinPending = true };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        var completed = await _account.TryCompleteJoinAsync(generation, CancellationToken.None);

        // Assert
        completed.Should().BeFalse();
        _sessions.Stored.Should().Be(again);
        _account.State.Should().Be(new JoinPending(Profile, Household));
    }

    [Fact]
    public async Task TryCompleteJoinAsync_SignedOutRightAfterTheJoinIsSaved_StaysSignedOut()
    {
        // Arrange: any read of the store after the join is saved is held until the sign-out has finished.
        var sessions = new HoldingSessions();
        using var account = new Account(sessions);
        await account.SignedInAsync(Session with { JoinPending = true }, CancellationToken.None);
        sessions.HoldReadsAfterTheNextSave();
        var join = account.TryCompleteJoinAsync(account.Generation, CancellationToken.None);

        // Act
        await account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        sessions.Release();
        await join;

        // Assert
        account.State.Should().Be(new SignedOut(SessionExpired: false));
        sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task TryCompleteJoinAsync_ExpiredRightAfterTheJoinIsSaved_SaysTheSessionExpired()
    {
        // Arrange
        var sessions = new HoldingSessions();
        using var account = new Account(sessions);
        await account.SignedInAsync(Session with { JoinPending = true }, CancellationToken.None);
        var generation = account.Generation;
        sessions.HoldReadsAfterTheNextSave();
        var join = account.TryCompleteJoinAsync(generation, CancellationToken.None);

        // Act
        await account.TryExpireAsync(generation, CancellationToken.None);
        sessions.Release();
        await join;

        // Assert
        account.State.Should().Be(new SignedOut(SessionExpired: true));
        sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfileAsync_AnotherAccountsProfile_ChangesNothing()
    {
        // Arrange: a slow Settings refresh for the account signed in before; another account is signed in now.
        await _account.SignedInAsync(Session, CancellationToken.None);
        var previousAccount = new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), "Ola", "ola@example.com");

        // Act
        await _account.UpdateProfileAsync(previousAccount, CancellationToken.None);

        // Assert
        _sessions.Stored.Should().Be(Session);
        _account.State.Should().Be(new SignedIn(Profile));
    }

    /// <summary>A session store whose reads after an armed save wait for <see cref="Release"/>, returning what was stored when they were made.</summary>
    private sealed class HoldingSessions : ISessionStore
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _armed;
        private bool _holding;

        public StoredSession? Stored { get; private set; }

        public void HoldReadsAfterTheNextSave() => _armed = true;

        public void Release() => _released.TrySetResult();

        public async Task<StoredSession?> GetAsync(CancellationToken cancellationToken)
        {
            var value = Stored;
            if (_holding)
                await _released.Task;
            return value;
        }

        public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
        {
            Stored = session;
            _holding |= _armed;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Stored = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TryClearPendingMoveAsync_SameGeneration_ClearsOnlyTheMarker()
    {
        // Arrange
        var other = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
        await _account.SignedInAsync(Session with { PendingMoveFrom = other }, CancellationToken.None);

        // Act
        var cleared = await _account.TryClearPendingMoveAsync(_account.Generation, CancellationToken.None);

        // Assert
        cleared.Should().BeTrue();
        _sessions.Stored.Should().Be(Session);
    }

    [Fact]
    public async Task TryClearPendingMoveAsync_AfterSigningInAgain_KeepsTheNewSessionsMarker()
    {
        // Arrange
        var other = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
        await _account.SignedInAsync(Session with { PendingMoveFrom = other }, CancellationToken.None);
        var generation = _account.Generation;
        await _account.SignedInAsync(Session with { PendingMoveFrom = other, AccessToken = "access-9" }, CancellationToken.None);

        // Act
        var cleared = await _account.TryClearPendingMoveAsync(generation, CancellationToken.None);

        // Assert
        cleared.Should().BeFalse();
        _sessions.Stored!.PendingMoveFrom.Should().Be(other);
    }
}
