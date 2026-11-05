using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Setup;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>"Already use Mochi? Sign in" on the first-launch step: someone with a family goes to it, not to a second baby.</summary>
[Collection(nameof(StringsState))]
public sealed class SetupSignInTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly HouseholdId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IAuthApi _auth = Substitute.For<IAuthApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public SetupSignInTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([
            HouseholdSettings.Create(Phone, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Device)).Value]);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Device);
        _google.GetIdTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("token");
    }

    public void Dispose()
    {
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private SetupViewModel Setup()
    {
        var time = new FakeTimeProvider(Now);
        var signIn = new SignIn(_auth, _google, _households, _device, Substitute.For<IDeviceName>(), _account);
        return new SetupViewModel(_households, new SetUpBaby(_households, Substitute.For<IVersionClock>(), time), time, _account, signIn, _sync);
    }

    private void Granted(HouseholdId household, SignInOutcome outcome) =>
        _auth.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).Returns(new SignInGrant(
            new SessionTokens("a", Now.AddMinutes(15), "r"), new AccountProfile(Me, "Kasia", "k@example.com"), household, outcome));

    [Fact]
    public async Task SignInCommand_AccountWithAFamily_SaysTheLogIsComingAndPullsIt()
    {
        // Arrange
        Granted(Family, SignInOutcome.Existing);
        var model = Setup();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Status.Should().Be("You're signed in. Getting your family's log…");
        model.IsFetching.Should().BeTrue();
        model.CanSignIn.Should().BeFalse();
        model.Error.Should().BeEmpty();
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task SignInCommand_NewAccount_AsksForTheBaby()
    {
        // Arrange
        Granted(Phone, SignInOutcome.Created);
        var model = Setup();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Status.Should().Be("Add your baby to start your family's log.");
        model.IsFetching.Should().BeFalse();
        model.CanSignIn.Should().BeFalse();
    }

    [Fact]
    public async Task AddBabyInsteadCommand_WhileFetching_BringsTheFormBack()
    {
        // Arrange
        Granted(Family, SignInOutcome.Existing);
        var model = Setup();
        await model.SignInCommand.ExecuteAsync(null);

        // Act
        model.AddBabyInsteadCommand.Execute(null);

        // Assert
        model.IsFetching.Should().BeFalse();
        model.Status.Should().BeEmpty();
        SetupViewModel.AddBabyInsteadText.Should().Be("Add a baby here instead");
    }

    [Fact]
    public async Task SignInCommand_Offline_SaysSoAndOffersItAgain()
    {
        // Arrange
        _auth.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException());
        var model = Setup();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().Be("Sign-in didn't work. Check your connection and try again.");
        model.Status.Should().BeEmpty();
        model.CanSignIn.Should().BeTrue();
        _sync.DidNotReceive().RequestSync();
    }

    [Fact]
    public async Task SignInCommand_Cancelled_ChangesNothing()
    {
        // Arrange
        _google.GetIdTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var model = Setup();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().BeEmpty();
        model.CanSignIn.Should().BeTrue();
        SetupViewModel.SignInText.Should().Be("Already use Mochi? Sign in");
    }
}
