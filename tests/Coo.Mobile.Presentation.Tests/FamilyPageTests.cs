using Coo.Contracts.Households;
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

/// <summary>Spec 011b §2's Family page: who sees what, the confirmations, and offline.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilyPageTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Andrzej = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));
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
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly ISyncControl _sync = SyncControls.PassThrough();
    private readonly List<Families> _made = [];

    public FamilyPageTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), Settings(Home)]);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _moves.SummaryAsync(default, default).ReturnsForAnyArgs(new LocalSummary(42, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Maja")]));
        _records.ListUnsharedEntriesAsync(default, default).ReturnsForAnyArgs([]);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Andrzej, "Andrzej", "a@example.com", Role.Owner, Now, false), new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)],
            [new FamilyInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), Role.Parent, Andrzej, Now, Now.AddHours(72))]));
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

    /// <summary>The Owner's view: Andrzej is the viewer, Kasia (a Parent) is the one who can be removed.</summary>
    private void OwnerSeesAParent() =>
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Andrzej, "Andrzej", "a@example.com", Role.Owner, Now, true), new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), false)],
            [new FamilyInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), Role.Parent, Andrzej, Now, Now.AddHours(72))]));

    private async Task<FamilyPageViewModel> PageAsync(Role myRole, int members, bool load = true)
    {
        await _account.SignedInAsync(new StoredSession("a", Now, "r", new AccountProfile(Me, "Kasia", "k@example.com"), Home, JoinPending: false), CancellationToken.None);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now.AddDays(-30), "Kasia", 1), new FamilyMembership(Joined, myRole, Now.AddDays(1), "Andrzej", members)]);
        var families = new Families(_account, _store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        _made.Add(families);
        _clock.Is24Hour.Returns(true);
        var page = new FamilyPageViewModel(new LoadFamilyMembers(_api), new RemoveFamilyMember(_api, families, _account), new WithdrawFamilyInvitation(_api),
            new InviteParent(_api), new LeaveFamily(_api, families, _sync, _account), families, _launcher, _clock);
        if (load)
            await page.LoadAsync(Joined);
        return page;
    }

    [Fact]
    public async Task LoadAsync_AsParent_ListsMembersWithRolesAndNoRemoveBesideTheOwner()
    {
        // Act
        var page = await PageAsync(Role.Parent, 2);

        // Assert
        page.Members.Should().BeEquivalentTo([
            new MemberRow(Andrzej, "Andrzej", "a@example.com", "Parent · created the family", CanRemove: false, "Andrzej"),
            new MemberRow(Me, "Kasia (you)", "k@example.com", "Parent", CanRemove: false, "Kasia")]);
        page.ShowsInvitations.Should().BeTrue();
        page.Invitations.Single().Line.Should().StartWith("Invitation for a parent · until ");
        page.ShowsLeave.Should().BeTrue("a Parent with another family may leave");
        page.ShowsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_Offline_ShowsTheLastListAndSaysItCouldNotRefresh()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.LoadAsync(Joined);

        // Assert
        page.RefreshFailed.Should().BeTrue();
        page.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveCommand_Offline_SaysItDidNotWorkAndChangesNothing()
    {
        // Arrange: Review Focus 4.
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        _api.RemoveMemberAsync(Joined, Me, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[1]);

        // Assert
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task RemovePrompt_AMember_NamesThemGenderNeutrally()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 2);

        // Act
        var prompt = page.RemovePrompt(page.Members[1]);

        // Assert
        prompt.Message.Should().Be("Remove Kasia from the family? Their phones stop sharing this family's log, and it is removed from them. Entries they added stay.");
        prompt.Accept.Should().Be("Remove");
    }

    [Fact]
    public async Task DeletePromptAsync_OwnerAloneWithUnsharedEntries_SaysWhatIsDeletedAndWhatIsLost()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1);
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), "diaper", Now, "x"),
            new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f2")), "diaper", Now, "x"),
            new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f3")), "diaper", Now, "x")]);

        // Act
        var prompt = await page.DeletePromptAsync();

        // Assert
        page.ShowsDelete.Should().BeTrue();
        prompt.Message.Should().Be(
            "Delete this family? Its log of Maja, 42 entries, is deleted from Coo's server and this phone. This can't be undone.\n\n"
            + "3 entries on this phone weren't shared yet and will be lost.");
    }

    [Fact]
    public async Task LeaveCommand_Answered_ClosesThePage()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(1);
    }

    [Fact]
    public async Task LeaveCommand_RefusedLastFamily_SaysWhyAndStays()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.LastFamily));

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        page.Toast.Should().Be("This is your only family, so it can't be left.");
    }

    private static readonly Guid InvitationId = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1");

    [Fact]
    public async Task LoadAsync_AsOwnerWithAParent_OffersRemoveOnTheOtherMemberOnly()
    {
        // Arrange
        OwnerSeesAParent();

        // Act
        var page = await PageAsync(Role.Owner, 2);

        // Assert
        page.Members.Select(m => m.CanRemove).Should().Equal(false, true);
        page.ShowsInvite.Should().BeTrue();
        page.ShowsDelete.Should().BeFalse("the Owner with other members cannot leave");
        page.ShowsLeave.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_AsCaregiver_OffersNoRemoveNoInvitationsNoInvite()
    {
        // Act
        var page = await PageAsync(Role.Caregiver, 2);

        // Assert
        page.Members.Should().OnlyContain(m => !m.CanRemove);
        page.Invitations.Should().BeEmpty();
        page.ShowsInvitations.Should().BeFalse();
        page.ShowsInvite.Should().BeFalse();
        page.ShowsLeave.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_WhenTheFamilyIsNotOnThePhone_Closes()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2, load: false);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home)]);

        // Act
        await page.LoadAsync(Joined);

        // Assert
        closed.Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_WhenTheLocalReadThrows_SaysItCouldNotRefreshInsteadOfCrashing()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        // Act
        Func<Task> act = async () => await page.LoadAsync(Joined);

        // Assert
        await act.Should().NotThrowAsync();
        page.RefreshFailed.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveCommand_AMember_RemovesThemAndReloads()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        var kasia = page.Members[1];

        // Act
        await page.RemoveCommand.ExecuteAsync(kasia);

        // Assert
        await _api.Received(1).RemoveMemberAsync(Joined, Me, Arg.Any<CancellationToken>());
        page.Toast.Should().BeEmpty();
        await _api.Received(2).ListMembersAsync(Joined, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveCommand_ARetriedRemove_CountsAsDone()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        _api.RemoveMemberAsync(Joined, Me, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.MemberNotFound));

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[1]);

        // Assert
        page.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveCommand_Refused_ShowsTheCodesString()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        _api.RemoveMemberAsync(Joined, Me, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.Forbidden));

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[1]);

        // Assert
        page.Toast.Should().Be("You can't do that in this family.");
    }

    [Fact]
    public async Task RemoveCommand_TheOwnerRow_IsNeverSentToTheServer()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[0]);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().RemoveMemberAsync(default, default, default);
        page.Toast.Should().Be("You can't do that in this family.");
    }

    [Fact]
    public async Task RemoveCommand_AsParentForAStaleRow_IsNeverSentToTheServer()
    {
        // Arrange: a row that claims CanRemove, on a page whose role has no right to it.
        var page = await PageAsync(Role.Caregiver, 2);

        // Act
        await page.RemoveCommand.ExecuteAsync(new MemberRow(Andrzej, "Andrzej", "a@example.com", "Parent", CanRemove: true, "Andrzej"));

        // Assert
        await _api.DidNotReceiveWithAnyArgs().RemoveMemberAsync(default, default, default);
    }

    [Fact]
    public async Task RemoveCommand_WhenSomethingUnexpectedThrows_ShowsTheToastInsteadOfCrashing()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        _api.RemoveMemberAsync(Joined, Me, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        // Act
        Func<Task> act = async () => await page.RemoveCommand.ExecuteAsync(page.Members[1]);

        // Assert
        await act.Should().NotThrowAsync();
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
        page.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveCommand_WhenTheReloadAfterwardsThrows_StillDoesNotCrash()
    {
        // Arrange
        OwnerSeesAParent();
        var page = await PageAsync(Role.Owner, 2);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        // Act
        Func<Task> act = async () => await page.RemoveCommand.ExecuteAsync(page.Members[1]);

        // Assert
        await act.Should().NotThrowAsync();
        page.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task WithdrawCommand_AnInvitation_WithdrawsIt()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);

        // Act
        await page.WithdrawCommand.ExecuteAsync(page.Invitations[0]);

        // Assert
        await _api.Received(1).WithdrawInvitationAsync(Joined, InvitationId, Arg.Any<CancellationToken>());
        page.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task WithdrawCommand_Offline_SaysItDidNotWork()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.WithdrawInvitationAsync(Joined, InvitationId, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.WithdrawCommand.ExecuteAsync(page.Invitations[0]);

        // Assert
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task WithdrawCommand_AsCaregiver_IsNeverSentToTheServer()
    {
        // Arrange
        var page = await PageAsync(Role.Caregiver, 2);

        // Act
        await page.WithdrawCommand.ExecuteAsync(new InvitationRow(InvitationId, "x"));

        // Assert
        await _api.DidNotReceiveWithAnyArgs().WithdrawInvitationAsync(default, default, default);
    }

    [Fact]
    public async Task WithdrawPrompt_Always_AsksToWithdraw()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);

        // Act
        var prompt = page.WithdrawPrompt;

        // Assert
        prompt.Message.Should().Be("Withdraw this invitation? Its link and code stop working.");
        prompt.Accept.Should().Be("Withdraw");
    }

    [Fact]
    public async Task InviteCommand_Created_OpensTheShareSheetWithTheShareText()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var created = new CreatedInvitation(InvitationId, InvitationCode.Parse("ABCDEFGH").Value, new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), Now.AddHours(72));
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).Returns(created);

        // Act
        await page.InviteCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).ShareTextAsync(Arg.Is<string>(t => t.Contains("ABCD-EFGH") && t.Contains("ABCDEFGH")), "Invite a parent");
    }

    [Fact]
    public async Task InviteCommand_TooManyOpen_ShowsTheCodesString()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationTooMany));

        // Act
        await page.InviteCommand.ExecuteAsync(null);

        // Assert
        page.Toast.Should().StartWith("This family already has 5 open invitations");
        await _launcher.DidNotReceiveWithAnyArgs().ShareTextAsync(default!, default!);
    }

    [Fact]
    public async Task InviteCommand_WhenTheShareSheetThrows_ShowsTheToastInsteadOfCrashing()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var created = new CreatedInvitation(InvitationId, InvitationCode.Parse("ABCDEFGH").Value, new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), Now.AddHours(72));
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).Returns(created);
        _launcher.ShareTextAsync(default!, default!).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("no sheet")));

        // Act
        Func<Task> act = async () => await page.InviteCommand.ExecuteAsync(null);

        // Assert
        await act.Should().NotThrowAsync();
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task InviteCommand_AsCaregiver_IsNeverSentToTheServer()
    {
        // Arrange
        var page = await PageAsync(Role.Caregiver, 2);

        // Act
        await page.InviteCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().CreateInvitationAsync(default, default);
    }

    [Fact]
    public async Task LeavePromptAsync_WithUnsharedEntries_AddsWhatWillBeLost()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), "diaper", Now, "x")]);

        // Act
        var prompt = await page.LeavePromptAsync();

        // Assert
        prompt.Message.Should().Be("Leave this family? Its log is removed from this phone. Entries you added stay with the family.\n\n1 entry on this phone wasn't shared yet and will be lost.");
        prompt.Accept.Should().Be("Leave");
    }

    [Fact]
    public async Task DeletePromptAsync_OwnerAloneWithNoBaby_UsesTheNoBabyText()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1);
        _moves.SummaryAsync(default, default).ReturnsForAnyArgs(new LocalSummary(1, []));

        // Act
        var prompt = await page.DeletePromptAsync();

        // Assert
        prompt.Message.Should().Be("Delete this family? Its log, 1 entry, is deleted from Coo's server and this phone. This can't be undone.");
    }

    [Fact]
    public async Task DeleteCommand_OwnerAlone_DeletesWithConfirmAndCloses()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Deleted);

        // Act
        await page.DeleteCommand.ExecuteAsync(null);

        // Assert
        await _api.Received(1).LeaveAsync(Joined, true, Arg.Any<CancellationToken>());
        closed.Should().Be(1);
    }

    [Fact]
    public async Task DeleteCommand_ConfirmationRequiredRefusal_SaysSoAndStays()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.ConfirmationRequired));

        // Act
        await page.DeleteCommand.ExecuteAsync(null);

        // Assert
        page.Toast.Should().Be("This family still has entries on Coo's server. Confirm to delete them.");
        closed.Should().Be(0);
    }

    [Fact]
    public async Task LeaveCommand_WhenTheClosedHandlerThrows_DoesNotCrash()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        page.Closed += (_, _) => throw new InvalidOperationException("pop failed");
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);

        // Act
        Func<Task> act = async () => await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadAsync_CaregiverViewerWithAParentMember_OffersNoRemove()
    {
        // Arrange
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Andrzej, "Andrzej", "a@example.com", Role.Parent, Now, false), new FamilyMember(Me, "Kasia", "k@example.com", Role.Caregiver, Now.AddDays(1), true)], []));

        // Act
        var page = await PageAsync(Role.Caregiver, 2);

        // Assert
        page.Members.Should().OnlyContain(m => !m.CanRemove);
    }

    [Fact]
    public async Task RemoveCommand_ParentViewerRemovingAnotherParent_ReachesTheServer()
    {
        // Arrange
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Andrzej, "Andrzej", "a@example.com", Role.Parent, Now, false), new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)], []));
        var page = await PageAsync(Role.Parent, 2);

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[0]);

        // Assert
        page.Members[0].CanRemove.Should().BeTrue();
        await _api.Received(1).RemoveMemberAsync(Joined, Andrzej, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteCommand_SoftLockedFamily_IsHiddenAndNeverSentToTheServer()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var page = await PageAsync(Role.Parent, 2);

        // Act
        await page.InviteCommand.ExecuteAsync(null);

        // Assert
        page.ShowsInvite.Should().BeFalse();
        await _api.DidNotReceiveWithAnyArgs().CreateInvitationAsync(default, default);
    }

    [Fact]
    public async Task LoadAsync_OwnerAloneWithNoOtherFamily_DoesNotOfferDelete()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1, load: false);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Joined, Role.Owner, Now.AddDays(1), "Kasia", 1)]);

        // Act
        await page.LoadAsync(Joined);

        // Assert
        page.ShowsDelete.Should().BeFalse();
        page.ShowsLeave.Should().BeFalse();
    }

    [Fact]
    public async Task LeaveCommand_Offline_SaysItDidNotWorkAndStays()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
        closed.Should().Be(0);
    }

    [Fact]
    public async Task LeaveCommand_WhenOnlyThisFamilyIsOnThePhone_IsNeverSentToTheServer()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2, load: false);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        await page.LoadAsync(Joined);

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        page.ShowsLeave.Should().BeFalse();
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task DeleteCommand_WhenTheOwnerStillHasMembers_IsNeverSentToTheServer()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 2);

        // Act
        await page.DeleteCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task LeaveCommand_WhenAnotherCommandIsRunning_DoesNothing()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var gate = new TaskCompletionSource<LeaveOutcome>();
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(gate.Task);
        var first = page.LeaveCommand.ExecuteAsync(null);

        // Act
        await page.LeaveCommand.ExecuteAsync(null);
        gate.SetResult(LeaveOutcome.Left);
        await first;

        // Assert
        await _api.Received(1).LeaveAsync(Joined, true, Arg.Any<CancellationToken>());
    }
}
