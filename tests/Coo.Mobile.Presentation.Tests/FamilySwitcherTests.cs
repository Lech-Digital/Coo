using Coo.Domain.CareLog;
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
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Walk fix F2: the family switcher's rows and the actions each family that is not shown gets.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilySwitcherTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly AccountProfile Profile = new(Me, "Kasia", "k@example.com");
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly HouseholdId Local = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly List<Families> _made = [];

    public FamilySwitcherTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _records.ListUnsharedEntriesAsync(default, default).ReturnsForAnyArgs([]);
        _sync.RunExclusiveAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
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
        var families = new Families(_account, _store, Substitute.For<IAccountApi>(), Substitute.For<IHouseholdApi>(), _sessions, _households, _moves, _state,
            _records, new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        _made.Add(families);
        var signIn = new SignIn(Substitute.For<IAuthApi>(), Substitute.For<ISignInProvider>(), _households, Substitute.For<IDeviceIdentity>(),
            Substitute.For<IDeviceName>(), _account);
        return new FamilySectionViewModel(families, _account, signIn, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()),
            _sessions, Substitute.For<IConsentPrompt>(), new InviteParent(Substitute.For<IHouseholdApi>(), Substitute.For<IInvitationCodes>()), Substitute.For<IExternalLauncher>(),
            Substitute.For<IClockFormat>(), _sync);
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    /// <summary>The first family is the shown one.</summary>
    private void OnPhone(params HouseholdId[] families)
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(Settings)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(families[0]);
    }

    private void Memberships(params FamilyMembership[] memberships) =>
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(memberships);

    private void Baby(HouseholdId household, string name) =>
        _households.ListBabiesAsync(household, Arg.Any<CancellationToken>()).Returns([
            BabyProfile.Create(new BabyId(Guid.CreateVersion7(Now)), household, name, [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Phone), new DateOnly(2026, 10, 2)).Value]);

    [Fact]
    public async Task RefreshAsync_TwoAccountFamilies_MarksTheShownOneAndOffersBringHereAndDeleteForMyOwnFamily()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        Baby(Home, "Lena");
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2));

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Select(r => r.Label).Should().Equal("Lena · Your family", "No baby yet · Family of Andrzej");
        var mine = section.Rows.Single(r => r.Id == Home);
        mine.IsShown.Should().BeFalse();
        mine.Actions.Should().Equal(FamilyRowAction.BringHere, FamilyRowAction.Delete);
        var shown = section.Rows.Single(r => r.Id == Joined);
        shown.IsShown.Should().BeTrue();
        shown.Actions.Should().BeEmpty("the shown family is only marked, never acted on from the switcher");
    }

    [Fact]
    public async Task RefreshAsync_JoinedFamilyNotShownAsParent_OffersLeave()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home, Joined);
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2));
        _moves.HasEverSyncedAsync(default, default).ReturnsForAnyArgs(true);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Single(r => r.Id == Joined).Actions.Should().Equal(FamilyRowAction.BringHere, FamilyRowAction.Leave);
    }

    [Fact]
    public async Task RefreshAsync_NeverSyncedFamilyOnThisPhoneOnly_OffersBringHereAndRemoveFromPhone()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home, Local);
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1));

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Single(r => r.Id == Local).Label.Should().Be("No baby yet · On this phone only");
        section.Rows.Single(r => r.Id == Local).Actions.Should().Equal(FamilyRowAction.BringHere, FamilyRowAction.RemoveFromPhone);
    }

    [Fact]
    public async Task RefreshAsync_FamilyOfAnotherAccount_IsNeverOfferedForBringingOver()
    {
        // Arrange: it synced, but under another account (spec §6.3).
        await SignInAsync();
        OnPhone(Home, Local);
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1));
        _moves.HasEverSyncedAsync(Local, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Single(r => r.Id == Local).Actions.Should().Equal(FamilyRowAction.RemoveFromPhone);
    }

    [Fact]
    public async Task RefreshAsync_ShownFamilyIsOnThisPhoneOnly_OffersNoBringingHere()
    {
        // Arrange: entries are only brought into an account family.
        await SignInAsync();
        OnPhone(Local, Home);
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1));

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Single(r => r.Id == Home).Actions.Should().BeEmpty("her only account family is not deleted or left from here, and nothing comes into a local family");
    }

    [Fact]
    public async Task RefreshAsync_SignedOutTwoFamilies_OffersRemovalOfTheOneNotShownOnly()
    {
        // Arrange
        OnPhone(Home, Local);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.Rows.Single(r => r.Id == Local).Actions.Should().Equal(FamilyRowAction.RemoveFromPhone);
        section.Rows.Single(r => r.Id == Home).Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovePromptAsync_NeverSyncedWithABaby_SaysTheLogWillBeGone()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home, Local);
        Baby(Local, "Lena");
        var section = Section();
        await section.RefreshAsync();

        // Act
        var prompt = await section.RemovePromptAsync(section.Rows.Single(r => r.Id == Local));

        // Assert
        prompt.Title.Should().Be("Remove from this phone");
        prompt.Message.Should().Be("Remove Lena's log from this phone? It isn't shared, so it will be gone.");
        prompt.Accept.Should().Be("Remove");
        prompt.Cancel.Should().Be("Cancel");
    }

    [Fact]
    public async Task RemovePromptAsync_SyncedUnderAnotherAccountWithUnsharedEntries_SaysItStaysWithThatAccountAndWhatIsLost()
    {
        // Arrange
        OnPhone(Home, Local);
        _moves.HasEverSyncedAsync(Local, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Local, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));
        _records.ListUnsharedEntriesAsync(Local, Arg.Any<CancellationToken>()).Returns([new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), "diaper", Now, "x")]);
        var section = Section();
        await section.RefreshAsync();

        // Act
        var prompt = await section.RemovePromptAsync(section.Rows.Single(r => r.Id == Local));

        // Assert
        prompt.Message.Should().Be(
            "Remove this family's log from this phone? It stays with the account it was shared from.\n\n1 entry on this phone wasn't shared yet and will be lost.");
    }

    [Fact]
    public async Task RemoveFromPhoneCommand_FamilyOnThisPhoneOnly_DeletesItInTheExclusiveSectionAndTellsThePages()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home, Local);
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.RemoveFromPhoneCommand.ExecuteAsync(section.Rows.Single(r => r.Id == Local));

        // Assert
        await _sync.Received(1).RunExclusiveAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _moves.Received(1).DeleteHouseholdAsync(Local, Arg.Any<CancellationToken>());
        _sync.Received(1).NotifyDataChanged();
        section.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFromPhoneCommand_AnAccountFamily_RemovesNothing()
    {
        // Arrange: a stale row can never take an account family off the phone this way.
        await SignInAsync();
        OnPhone(Joined, Home);
        Memberships(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2));
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.RemoveFromPhoneCommand.ExecuteAsync(section.Rows.Single(r => r.Id == Home));

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        section.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task RemoveFromPhoneCommand_TheDeleteFails_SaysItDidNotWork()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home, Local);
        _moves.DeleteHouseholdAsync(Local, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("disk"));
        var section = Section();
        await section.RefreshAsync();

        // Act
        Func<Task> act = async () => await section.RemoveFromPhoneCommand.ExecuteAsync(section.Rows.Single(r => r.Id == Local));

        // Assert
        await act.Should().NotThrowAsync();
        section.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }
}
