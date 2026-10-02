using Coo.Api.Application.Auth;
using Coo.Domain.Common;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §3.3: rotation on every use, reuse detection, the 180-day sliding lifetime, sign-out.</summary>
public sealed class SessionTests
{
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_CurrentToken_RotatesAndIssuesNewTokens()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromMinutes(20));

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RefreshToken.Should().NotBe(signedIn.Session.RefreshToken);
        result.Value.AccessToken.ExpiresAt.Should().Be(_kit.Time.GetUtcNow() + TimeSpan.FromMinutes(15));
        var row = _kit.Sessions.Of(signedIn.Session.Id);
        row.RefreshHash.Should().Be(AuthSecrets.Hash(result.Value.RefreshToken));
        row.PreviousRefreshHash.Should().Be(AuthSecrets.Hash(signedIn.Session.RefreshToken));
        row.Session.LastUsedAt.Should().Be(_kit.Time.GetUtcNow());
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenPresentedAgain_RevokesTheSession()
    {
        // Arrange: someone kept the old refresh token and uses it after the phone rotated it.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var rotated = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Act
        var reused = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        reused.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
        (await _kit.Refresh().ExecuteAsync(rotated.Value.RefreshToken, CancellationToken.None)).Error!.Code
            .Should().Be(AuthErrors.RefreshInvalid, "the thief's use revokes the phone's session too");
    }

    [Fact]
    public async Task ExecuteAsync_After180DaysIdle_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromDays(180) + TimeSpan.FromMinutes(1));

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_UsedWithin180Days_SlidesTheLifetime()
    {
        // Arrange: used on day 179, then again 179 days later; 358 days in all.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromDays(179));
        var first = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromDays(179));

        // Act
        var second = await _kit.Refresh().ExecuteAsync(first.Value.RefreshToken, CancellationToken.None);

        // Assert
        second.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task ExecuteAsync_UnknownToken_IsRefused(string token)
    {
        // Arrange
        await _kit.SignInAsync("google-1", Phone, Household);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(token, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AfterSignOut_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.SignOut().ExecuteAsync(signedIn.Session.Id, CancellationToken.None);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        (await _kit.Sessions.IsActiveAsync(signedIn.Session.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NoncesOlderThanADay_AreDeletedAndNewerKept()
    {
        // Arrange
        await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(25));
        var fresh = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var deleted = await _kit.Purge().ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        _kit.Nonces.Rows.Keys.Should().Equal(AuthSecrets.Hash(fresh));
    }
}
