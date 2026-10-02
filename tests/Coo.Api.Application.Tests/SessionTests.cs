using Coo.Api.Application.Auth;
using Coo.Domain.Common;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §3.3: rotation on every use, reuse detection, the 180-day sliding lifetime, sign-out.</summary>
public sealed class SessionTests
{
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));

    private static readonly DeviceId OtherPhone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));
    private static readonly HouseholdId OtherHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly string NextToken = new('n', 43);
    private static readonly string OtherNextToken = new('m', 43);

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

    [Fact]
    public async Task ExecuteAsync_CurrentTokenWithAProposal_RotatesToTheProposedToken()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        result.Value.RefreshToken.Should().Be(NextToken);
        _kit.Sessions.Of(signedIn.Session.Id).RefreshHash.Should().Be(AuthSecrets.Hash(NextToken));
    }

    [Fact]
    public async Task ExecuteAsync_SamePairRetriedAfterALostResponse_IssuesAccessAndChangesNothing()
    {
        // Arrange: the server rotated T1 to T2, the response was lost, the phone sends T1 + T2 again.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);
        var before = _kit.Sessions.Of(signedIn.Session.Id);
        _kit.Time.Advance(TimeSpan.FromSeconds(30));

        // Act
        var retried = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        retried.IsSuccess.Should().BeTrue();
        retried.Value.RefreshToken.Should().Be(NextToken);
        retried.Value.AccessToken.ExpiresAt.Should().Be(_kit.Time.GetUtcNow() + TimeSpan.FromMinutes(15));
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before, "the retry changes nothing");
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenWithAnotherProposal_RevokesTheSession()
    {
        // Arrange: someone holding T1 cannot know T2.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Act
        var stolen = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, OtherNextToken, CancellationToken.None);

        // Assert
        stolen.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenWithoutAProposal_StillRevokes()
    {
        // Arrange: a 0.10.0 phone, or a thief that sends no proposal.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Act
        var reused = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        reused.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task ExecuteAsync_ProposalTooShort_IsRefusedAndChangesNothing(string proposal)
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var before = _kit.Sessions.Of(signedIn.Session.Id);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, proposal, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalEqualToThePresentedToken_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var before = _kit.Sessions.Of(signedIn.Session.Id);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalAtTheMaximumLength_RotatesToIt()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var longest = new string('n', RefreshSession.NextTokenMaxLength);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, longest, CancellationToken.None);

        // Assert
        result.Value.RefreshToken.Should().Be(longest);
        _kit.Sessions.Of(signedIn.Session.Id).RefreshHash.Should().Be(AuthSecrets.Hash(longest));
    }

    [Fact]
    public async Task ExecuteAsync_ProposalOverTheMaximumLength_IsRefusedAndChangesNothing()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var before = _kit.Sessions.Of(signedIn.Session.Id);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(
            signedIn.Session.RefreshToken, new string('n', RefreshSession.NextTokenMaxLength + 1), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalAnotherSessionHolds_IsRefusedAndChangesNothing()
    {
        // Arrange: the store's unique refresh hash refuses the swap; the phone must hear refresh-invalid, not an error.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var other = await _kit.SignInAsync("google-2", OtherPhone, OtherHousehold);
        var before = _kit.Sessions.Of(signedIn.Session.Id);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, other.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before);
        _kit.Sessions.Of(other.Session.Id).Session.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_LostTheSwapToAnotherProposal_IsRefusedWithoutRevoking()
    {
        // Arrange: a concurrent request with T1 rotated to a different next token; this one is not its retry.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Sessions.BeforeRotate = () => _kit.Sessions.RotateDirectly(signedIn.Session.Id, AuthSecrets.Hash(signedIn.Session.RefreshToken), AuthSecrets.Hash(OtherNextToken));

        // Act
        var second = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        second.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeFalse();
        _kit.Sessions.Of(signedIn.Session.Id).RefreshHash.Should().Be(AuthSecrets.Hash(OtherNextToken));
    }

    [Fact]
    public async Task ExecuteAsync_TwoIdenticalRequestsRacing_BothSucceed()
    {
        // Arrange: the phone's retry arrives while the first request is still rotating. The fake's rotation is
        // the store's compare-and-swap; the second request finds T1 current, loses the swap, and must see the retry.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Sessions.BeforeRotate = () => _kit.Sessions.RotateDirectly(signedIn.Session.Id, AuthSecrets.Hash(signedIn.Session.RefreshToken), AuthSecrets.Hash(NextToken));

        // Act
        var second = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        second.IsSuccess.Should().BeTrue();
        second.Value.RefreshToken.Should().Be(NextToken);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeFalse();
    }
}
