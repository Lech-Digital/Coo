using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>The session store, in memory: an outbound port.</summary>
internal sealed class MemorySessions : ISessionStore
{
    public StoredSession? Stored { get; private set; }

    public Task<StoredSession?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        Stored = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Stored = null;
        return Task.CompletedTask;
    }
}

/// <summary>Spec 010 §2, §3.2 (what each outcome does on the phone) and §3.3. Only the outbound ports are substitutes.</summary>
public sealed class AuthUseCaseTests : IDisposable
{
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Local = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId AccountsHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);
    private static readonly SessionTokens Tokens = new("access-1", Expires, "refresh-1");

    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly IAccountApi _accountApi = Substitute.For<IAccountApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly IDeviceName _deviceName = Substitute.For<IDeviceName>();
    private readonly MemorySessions _sessions = new();
    private readonly Account _account;

    public AuthUseCaseTests()
    {
        _account = new Account(_sessions);
        var settings = HouseholdSettings.Create(Local, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Device);
        _deviceName.Name.Returns("Pixel 10");
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).Returns("nonce-1");
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns("id-token");
    }

    public void Dispose() => _account.Dispose();

    [Theory]
    [InlineData(SignInOutcome.Adopted)]
    [InlineData(SignInOutcome.Existing)]
    public async Task ExecuteAsync_OutcomeKeepsThePhonesHousehold_SignsInWithoutRekeyingAnything(SignInOutcome outcome)
    {
        // Arrange
        Grants(outcome, Local);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.AccountHousehold.Should().Be(Local);
        await _api.Received(1).SignInWithGoogleAsync(new GoogleSignIn("id-token", "Pixel 10", Device, Local), Arg.Any<CancellationToken>());
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_Created_SignsInAndRecordsTheNewHousehold()
    {
        // Arrange: Increment 11 moves the local records to the new ID before the first sync.
        Grants(SignInOutcome.Created, AccountsHousehold);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.AccountHousehold.Should().Be(AccountsHousehold);
        _sessions.Stored.JoinPending.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Created_RecordsTheMoveFromThisPhonesHousehold()
    {
        // Arrange: the marker the sync cycle needs before it moves anything (Spec 011 §4, review fix).
        Grants(SignInOutcome.Created, AccountsHousehold);

        // Act
        await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        _sessions.Stored!.PendingMoveFrom.Should().Be(Local);
    }

    [Theory]
    [InlineData(SignInOutcome.Adopted, false)]
    [InlineData(SignInOutcome.Existing, false)]
    [InlineData(SignInOutcome.Existing, true)]
    [InlineData(SignInOutcome.Joined, true)]
    public async Task ExecuteAsync_OutcomeOtherThanCreated_RecordsNoMove(SignInOutcome outcome, bool otherHousehold)
    {
        // Arrange
        Grants(outcome, otherHousehold ? AccountsHousehold : Local);

        // Act
        await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        _sessions.Stored!.PendingMoveFrom.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SignOutAfterCreated_ForgetsTheMove()
    {
        // Arrange
        Grants(SignInOutcome.Created, AccountsHousehold);
        await SignIn().ExecuteAsync(CancellationToken.None);

        // Act
        await new SignOut(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert: the marker lives in the session, so every sign-out path takes it with the session.
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_Joined_IsJoinPendingAndKeepsThePhonesEntries()
    {
        // Arrange: the same account on a second phone (Q1).
        Grants(SignInOutcome.Joined, AccountsHousehold);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new JoinPending(Profile, AccountsHousehold));
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingWithAnotherHousehold_IsJoinPending()
    {
        // Arrange: this phone signed out while join-pending and signs in again; it must still be asked (ruling 9).
        Grants(SignInOutcome.Existing, AccountsHousehold);

        // Act
        await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new JoinPending(Profile, AccountsHousehold));
    }

    [Fact]
    public async Task ExecuteAsync_SheetCancelled_ChangesNothing()
    {
        // Arrange
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Cancelled);
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        await _api.DidNotReceiveWithAnyArgs().SignInWithGoogleAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnreachable_FailsAndStoresNothing()
    {
        // Arrange
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ServerRefusesTheToken_FailsAndStaysSignedOut()
    {
        // Arrange
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException("auth.google-token-invalid"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
        _account.State.Should().BeOfType<SignedOut>();
    }

    [Fact]
    public async Task ExecuteAsync_GoogleSheetFails_Fails()
    {
        // Arrange: no Google account on the phone, Play services missing, and so on.
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).ThrowsAsync(new SignInUnavailableException("NoCredentialException"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_SignOut_RevokesOnTheServerAndSignsOutHere()
    {
        // Arrange
        await SignedInAsync();

        // Act
        await new SignOut(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        await _accountApi.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnreachable_StillSignsOutHere()
    {
        // Arrange: the person asked to sign out; the server's session ends on its own (180 days idle).
        await SignedInAsync();
        _accountApi.SignOutAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await new SignOut(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAccountSucceeds_ClearsTheSessionOnly()
    {
        // Arrange: the local log stays (Q2); DeleteAccount has no access to it at all.
        await SignedInAsync();

        // Act
        var deleted = await new DeleteAccount(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().BeTrue();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAccountOffline_FailsAndStaysSignedIn()
    {
        // Arrange
        await SignedInAsync();
        _accountApi.DeleteAccountAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var deleted = await new DeleteAccount(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().BeFalse();
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountWithANewName_UpdatesTheProfile()
    {
        // Arrange
        await SignedInAsync();
        var renamed = Profile with { Name = "Andrzej L." };
        _accountApi.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(renamed, Local, "owner", []));

        // Act
        await new RefreshAccount(_accountApi, _sessions, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(renamed));
        _sessions.Stored!.AccessToken.Should().Be("access-1");
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountOffline_KeepsTheAccount()
    {
        // Arrange: airplane mode (deliverable 5).
        await SignedInAsync();
        _accountApi.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await new RefreshAccount(_accountApi, _sessions, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountSignedOut_AsksNothing()
    {
        // Arrange
        var refresh = new RefreshAccount(_accountApi, _sessions, _account);

        // Act
        await refresh.ExecuteAsync(CancellationToken.None);

        // Assert
        await _accountApi.DidNotReceiveWithAnyArgs().GetMeAsync(default);
    }

    [Fact]
    public async Task LoadAsync_StoredJoinedSession_IsJoinPending()
    {
        // Arrange: the app restarted after signing in on the second phone.
        await _sessions.SaveAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);

        // Act
        var state = await _account.LoadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(new JoinPending(Profile, AccountsHousehold));
    }

    private SignIn SignIn() => new(_api, _google, _households, _device, _deviceName, _account);

    private void Grants(SignInOutcome outcome, HouseholdId household) =>
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).Returns(new SignInGrant(Tokens, Profile, household, outcome));

    private Task SignedInAsync() =>
        _account.SignedInAsync(new StoredSession("access-1", Expires, "refresh-1", Profile, Local, JoinPending: false), CancellationToken.None);
}
