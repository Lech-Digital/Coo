using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 010 §2 and §5: the Account section's states and texts; the deletion confirmation names the baby.</summary>
[Collection(nameof(StringsState))]
public sealed class AccountViewModelTests : IDisposable
{
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId AccountsHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);

    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly IAccountApi _accountApi = Substitute.For<IAccountApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly IDeviceName _deviceName = Substitute.For<IDeviceName>();
    private readonly MemorySessions _sessions = new();
    private readonly ISyncStatus _sync = Substitute.For<ISyncStatus>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly Account _account;

    public AccountViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _sync.Status.Returns(SyncStatus.Initial);
        _account = new Account(_sessions);
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), Household, "Maja", [],
            new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 2)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Device);
        _deviceName.Name.Returns("Pixel 10");
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).Returns("nonce-1");
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns("id-token");
    }

    public void Dispose()
    {
        Strings.Use(LanguagePreference.System, null);
        _account.Dispose();
    }

    [Fact]
    public void Constructor_SignedOut_OffersSignInWithTheHint()
    {
        // Arrange
        var account = _account;

        // Act
        using var model = Model();

        // Assert
        account.State.Should().BeOfType<SignedOut>();
        model.IsSignedIn.Should().BeFalse();
        model.StatusLine.Should().Be("Keep your log safe and share it with family.");
    }

    [Fact]
    public async Task SignInCommand_Succeeds_ShowsTheNameAndEmail()
    {
        // Arrange
        using var model = Model();
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>())
            .Returns(new SignInGrant(new SessionTokens("a", Expires, "r"), Profile, Household, SignInOutcome.Adopted));

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.Name.Should().Be("Andrzej");
        model.Email.Should().Be("andrzej@example.com");
        model.StatusLine.Should().BeEmpty();
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInCommand_Fails_SaysSoAndStaysSignedOut()
    {
        // Arrange
        using var model = Model();
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.Toast.Should().Be("Sign-in didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task SignInCommand_Cancelled_SaysNothing()
    {
        // Arrange
        using var model = Model();
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task State_JoinPending_SaysTheEntriesAreNotInTheFamilysLogYet()
    {
        // Arrange
        using var model = Model();

        // Act
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.StatusLine.Should().Be("Signed in. The entries on this phone aren't in your family's log yet.");
    }

    [Fact]
    public async Task State_SessionRefused_AsksToSignInAgain()
    {
        // Arrange: the refreshing handler found the session revoked.
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        await _account.SignedOutAsync(sessionExpired: true, CancellationToken.None);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.StatusLine.Should().Be("Sign in again to keep sharing.");
    }

    [Fact]
    public async Task DeletePromptAsync_SignedIn_NamesTheBaby()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        var prompt = await model.DeletePromptAsync();

        // Assert
        prompt.Message.Should().Be(
            "This deletes your Coo account and, because you are the only parent, your family's shared log for Maja. The entries on this phone stay here.");
        prompt.Accept.Should().Be("Delete account");
        prompt.Cancel.Should().Be("Cancel");
    }

    [Fact]
    public async Task DeletePromptAsync_JoinPending_DoesNotNameThisPhonesBaby()
    {
        // Arrange: this phone's Maja is not in the account's household, which is the one that goes.
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);
        using var model = Model();

        // Act
        var prompt = await model.DeletePromptAsync();

        // Assert
        prompt.Message.Should().NotContain("Maja").And.EndWith("The entries on this phone stay here.");
    }

    [Fact]
    public void SignOutPrompt_Always_SaysTheLogStays()
    {
        // Arrange
        using var model = Model();

        // Act
        var prompt = model.SignOutPrompt;

        // Assert
        prompt.Should().Be(new ConfirmPrompt("Sign out", "Sign out on this phone? Your log stays on this phone.", "Sign out", "Cancel"));
    }

    [Fact]
    public async Task SignOutCommand_SignedIn_ShowsSignedOutAndTouchesNoLocalData()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        await model.SignOutCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
        await _households.DidNotReceiveWithAnyArgs().SaveBabyAsync(default!, default);
    }

    [Fact]
    public async Task DeleteAccountCommand_Offline_SaysItWasNotDeleted()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        _accountApi.DeleteAccountAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));
        using var model = Model();

        // Act
        await model.DeleteAccountCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.Toast.Should().Be("Your account wasn't deleted. Check your connection and try again.");
    }

    [Fact]
    public async Task SignInCommand_FailsTwice_NotifiesToastEachTime()
    {
        // Arrange
        using var model = Model();
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));
        var notifications = 0;
        model.PropertyChanged += (_, e) => notifications += e.PropertyName == nameof(AccountViewModel.Toast) && model.Toast.Length > 0 ? 1 : 0;

        // Act
        await model.SignInCommand.ExecuteAsync(null);
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        notifications.Should().Be(2);
    }

    [Fact]
    public async Task SignInCommand_SucceedsAfterFailure_ClearsTheOldToast()
    {
        // Arrange
        using var model = Model();
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromException<string>(new AuthUnavailableException("offline")), _ => Task.FromResult("nonce-1"));
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>())
            .Returns(new SignInGrant(new SessionTokens("a", Expires, "r"), Profile, Household, SignInOutcome.Adopted));
        await model.SignInCommand.ExecuteAsync(null);

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAccountCommand_TappedTwiceWhileRunning_CallsTheServerOnce()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        var gate = new TaskCompletionSource();
        _accountApi.DeleteAccountAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        using var model = Model();

        // Act
        var first = model.DeleteAccountCommand.ExecuteAsync(null);
        var second = model.DeleteAccountCommand.ExecuteAsync(null);
        gate.SetResult();
        await Task.WhenAll(first, second);

        // Assert
        await _accountApi.Received(1).DeleteAccountAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LineActionCommand_SharingOff_AsksForTheConsentSheet()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        _sync.Status.Returns(SyncStatus.Initial with { Last = CycleOutcome.ConsentDeclined });
        using var model = Model();
        var asked = 0;
        model.ConsentRequested += (_, _) => asked++;

        // Act
        model.LineActionCommand.Execute(null);

        // Assert
        model.LineActionText.Should().Be("Turn on sharing");
        asked.Should().Be(1);
    }

    [Fact]
    public async Task StatusChanged_FromTheEngine_RaisesTheLineProperties()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _sync.Status.Returns(SyncStatus.Initial with { Last = CycleOutcome.Failed, Pending = 2 });

        // Act
        _sync.StatusChanged += Raise.Event();

        // Assert
        changed.Should().Contain(nameof(AccountViewModel.StatusLine));
        model.StatusLine.Should().Be("Waiting for a connection. 2 changes are safe on this phone.");
    }

    [Fact]
    public async Task LineActionCommand_JoinPending_AsksForTheBringOverSheet()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);
        using var model = Model();
        var asked = 0;
        model.BringOverRequested += (_, _) => asked++;

        // Act
        model.LineActionCommand.Execute(null);

        // Assert
        model.LineActionText.Should().Be("Choose what to do with them");
        asked.Should().Be(1);
    }

    [Fact]
    public async Task StatusLine_SyncStoppedForGood_IsNotTheWaitingLine()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        _sync.Status.Returns(SyncStatus.Initial with { Last = CycleOutcome.Failed, LastError = SyncCycle.NoProgressError });

        // Act
        using var model = Model();

        // Assert
        model.StatusLine.Should().StartWith("Sharing stopped");
    }

    private AccountViewModel Model() => new(
        _account,
        new SignIn(_api, _google, _households, _device, _deviceName, _account),
        new SignOut(_accountApi, _account),
        new DeleteAccount(_accountApi, _account),
        new RefreshAccount(_accountApi, _sessions, _account),
        _households,
        _sync,
        _clock);

    private sealed class MemorySessions : ISessionStore
    {
        private StoredSession? _stored;

        public Task<StoredSession?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
        {
            _stored = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            _stored = null;
            return Task.CompletedTask;
        }
    }
}
