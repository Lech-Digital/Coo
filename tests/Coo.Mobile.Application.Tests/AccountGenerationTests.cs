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
}
