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
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §2's Settings → Family rows, and inviting from them.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilySectionTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly AccountProfile Profile = new(Me, "Kasia", "k@example.com");
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IConsentPrompt _consentPrompt = Substitute.For<IConsentPrompt>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly List<Families> _made = [];

    public FamilySectionTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _clock.Is24Hour.Returns(true);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
    }

    public void Dispose()
    {
        foreach (var families in _made)
            families.Dispose();
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    private FamilySectionViewModel Section()
    {
        var families = new Families(_account, _store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, _state,
            Substitute.For<IFamilyRecords>(), new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        _made.Add(families);
        var signIn = new SignIn(Substitute.For<IAuthApi>(), Substitute.For<ISignInProvider>(), _households, Substitute.For<IDeviceIdentity>(),
            Substitute.For<IDeviceName>(), _account);
        return new FamilySectionViewModel(families, _account, signIn, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()),
            _sessions, _consentPrompt, new InviteParent(_api), _launcher, _clock, _sync);
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    private void OnPhone(params HouseholdId[] families)
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(Settings)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(families[0]);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
    }

    [Fact]
    public async Task RefreshAsync_OneLocalFamilySignedOut_ShowsOnlyHereInviteAndJoin()
    {
        // Arrange
        OnPhone(Home);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsSwitcher.Should().BeFalse();
        section.ShowsMembers.Should().BeFalse();
        section.ShowsOnlyHere.Should().BeTrue();
        section.ShowsInvite.Should().BeTrue("signed out, Invite a parent signs in first (§3)");
        section.ShowsBringOver.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_TwoFamiliesShownSharedAsParent_ShowsTheSwitcherMembersAndInvite()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsSwitcher.Should().BeTrue();
        section.ShowingTitle.Should().Be("Showing: No baby yet");
        section.ShowingLine.Should().Be("Family of Andrzej");
        section.ShowsMembers.Should().BeTrue();
        section.MembersLine.Should().Be("2 people");
        section.ShowsInvite.Should().BeTrue();
        section.ShowsBringOver.Should().BeTrue("the shown family synced and is an account family, and another account family exists");
        section.Choices.Select(c => c.Label).Should().Equal("No baby yet · Your family", "No baby yet · Family of Andrzej");
    }

    [Fact]
    public async Task RefreshAsync_ShownFamilySoftLocked_HidesInviteAndLeavesItOutOfTheSwitcher()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsInvite.Should().BeFalse();
        section.Choices.Select(c => c.Id).Should().Equal(Home);
    }

    [Fact]
    public async Task InviteCommand_SignedInShownShared_SharesTheTextWithLinkCodeAndExpiry()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _api.CreateInvitationAsync(Home, Arg.Any<CancellationToken>()).Returns(new CreatedInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"),
            InvitationCode.Parse("ABCDEFGH").Value, new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), Now.AddHours(72)));
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).ShareTextAsync(Arg.Is<string>(t => t.Contains("https://api.coo.lechdigital.nl/i/ABCDEFGH") && t.Contains("ABCD-EFGH")), "Invite a parent");
        section.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task InviteCommand_ConsentRefused_InvitesNobody()
    {
        // Arrange: §3, consent if the account has none, then carries on.
        await SignInAsync();
        OnPhone(Home);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consentPrompt.AskAsync().Returns(false);
        var section = Section();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().CreateInvitationAsync(default, default);
        await _launcher.DidNotReceiveWithAnyArgs().ShareTextAsync(default!, default!);
    }

    [Fact]
    public async Task InviteCommand_Offline_SaysItDidNotWork()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _api.CreateInvitationAsync(Home, Arg.Any<CancellationToken>()).Returns(Task.FromException<CreatedInvitation>(new HouseholdUnavailableException("offline")));
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        section.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task SwitchCommand_AnotherFamily_ActivatesItAndTellsThePages()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        var section = Section();

        // Act
        await section.SwitchCommand.ExecuteAsync(new FamilyChoice(Home, "Maja · Your family"));

        // Assert
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
        _sync.Received(1).NotifyDataChanged();
    }
}
