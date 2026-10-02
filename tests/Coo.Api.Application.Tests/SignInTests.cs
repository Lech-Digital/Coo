using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §3.1 (fail closed) and §3.2 (the four outcomes). The Google verifier is a fake of the port.</summary>
public sealed class SignInTests
{
    private static readonly DeviceId PhoneA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DeviceId PhoneB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));
    private static readonly HouseholdId HouseholdA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId HouseholdB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_NewUserUnusedHouseholdId_AdoptsThePhonesHouseholdAsOwner()
    {
        // Arrange
        var subject = "google-1";

        // Act
        var result = await _kit.SignInAsync(subject, PhoneA, HouseholdA);

        // Assert
        result.Outcome.Should().Be(SignInOutcome.Adopted);
        result.Household.Should().Be(HouseholdA);
        _kit.Accounts.Memberships.Should().ContainSingle().Which.Should().Match<Membership>(m =>
            m.HouseholdId == HouseholdA && m.UserId == result.User.Id && m.Role == Role.Owner);
        _kit.Sessions.Rows.Should().ContainSingle().Which.Session.DeviceName.Should().Be("Pixel 10");
    }

    [Fact]
    public async Task ExecuteAsync_NewUserHouseholdIdTaken_CreatesAHouseholdWithANewId()
    {
        // Arrange: practically never (UUIDv7), but the server must not join a stranger's household.
        _kit.Accounts.Households.Add(HouseholdA);

        // Act
        var result = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        result.Outcome.Should().Be(SignInOutcome.Created);
        result.Household.Should().NotBe(HouseholdA);
        _kit.Accounts.Households.Should().Contain(result.Household);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUserSamePhone_IsExistingAndKeepsOneSessionForThePhone()
    {
        // Arrange
        var first = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var again = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        again.Outcome.Should().Be(SignInOutcome.Existing);
        again.Household.Should().Be(first.Household);
        _kit.Sessions.Rows.Should().ContainSingle().Which.Session.Id.Should().Be(again.Session.Id);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUserOtherPhone_IsJoinedAndAddsOnlyASession()
    {
        // Arrange: the owner's account on his girlfriend's phone, which has its own provisional household.
        var first = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var second = await _kit.SignInAsync("google-1", PhoneB, HouseholdB);

        // Assert
        second.Outcome.Should().Be(SignInOutcome.Joined);
        second.Household.Should().Be(first.Household);
        _kit.Accounts.Households.Should().Equal(HouseholdA);
        _kit.Sessions.Rows.Select(r => r.Session.DeviceId).Should().BeEquivalentTo([PhoneA, PhoneB]);
    }

    [Fact]
    public async Task ExecuteAsync_SamePhoneAfterCreated_IsExisting()
    {
        // Arrange: the phone's ID was taken, so the account's household has another ID; the phone still sends its own.
        _kit.Accounts.Households.Add(HouseholdA);
        await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var again = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        again.Outcome.Should().Be(SignInOutcome.Existing, "the device has had a session for this user before");
        _kit.Sessions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_UsedNonce_IsRefusedAndCreatesNothing()
    {
        // Arrange: a second Google token carrying a nonce that the first sign-in consumed.
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);
        var replay = new GoogleSignInCommand(_kit.Google.Issue("google-2", nonce), "Pixel 9", PhoneB, HouseholdB);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(replay, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.BySubject.Should().NotContainKey("google-2");
        _kit.Sessions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_NonceOlderThanFiveMinutes_IsRefusedAndCreatesNothing()
    {
        // Arrange
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // Act
        var result = await _kit.SignIn().ExecuteAsync(
            new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.BySubject.Should().BeEmpty();
        _kit.Sessions.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownNonce_IsRefusedAndCreatesNothing()
    {
        // Arrange: a valid Google token whose nonce this server never issued.
        var token = _kit.Google.Issue("google-1", "a-nonce-from-elsewhere");

        // Act
        var result = await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand(token, "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.Households.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TokenTheVerifierRejects_IsRefusedAndLeavesTheNonceUnused()
    {
        // Arrange: a bad signature, the wrong audience, an unverified email, or Google's keys unreachable all look
        // the same here: the verifier returns nothing.
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand("forged", "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Nonces.Rows[AuthSecrets.Hash(nonce)].ConsumedAt.Should().BeNull();
        _kit.Accounts.BySubject.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NewSession_StoresOnlyTheRefreshTokensHash()
    {
        // Arrange
        var subject = "google-1";

        // Act
        var result = await _kit.SignInAsync(subject, PhoneA, HouseholdA);

        // Assert
        var row = _kit.Sessions.Of(result.Session.Id);
        row.RefreshHash.Should().Be(AuthSecrets.Hash(result.Session.RefreshToken)).And.NotBe(result.Session.RefreshToken);
        result.Session.AccessToken.ExpiresAt.Should().Be(AuthKit.Start + TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData("", "Phone")]
    [InlineData("   ", "Phone")]
    [InlineData("  Pixel 10  ", "Pixel 10")]
    public async Task ExecuteAsync_DeviceName_IsTrimmedWithAFallback(string sent, string stored)
    {
        // Arrange
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(
            new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), sent, PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        _kit.Sessions.Of(result.Value.Session.Id).Session.DeviceName.Should().Be(stored);
    }

    [Fact]
    public async Task ExecuteAsync_DeviceNameLongerThanSixtyCharacters_IsTruncatedToSixty()
    {
        // Arrange
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        var longName = new string('x', SignInWithGoogle.DeviceNameMaxLength + 15);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(
            new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), longName, PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        _kit.Sessions.Of(result.Value.Session.Id).Session.DeviceName.Should().Be(new string('x', 60));
    }

    [Fact]
    public async Task ExecuteAsync_IssuedNonce_StoresOnlyItsHash()
    {
        // Arrange
        var issue = _kit.IssueNonce();

        // Act
        var nonce = await issue.ExecuteAsync(CancellationToken.None);

        // Assert
        _kit.Nonces.Rows.Should().ContainKey(AuthSecrets.Hash(nonce)).And.NotContainKey(nonce);
        nonce.Length.Should().Be(43, "256 bits in base64url without padding");
    }
}
