using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §4–§6 on the phone: the pending invitation, each answer, the choice.</summary>
public sealed class JoinFamilyTests : IDisposable
{
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;
    private static readonly BabyId MyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly ISyncStore _syncStore = Substitute.For<ISyncStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly FakeTimeProvider _time = new(Now);

    public JoinFamilyTests()
    {
        _account = new Account(_sessions);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Home);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
    }

    public void Dispose() => _account.Dispose();

    private JoinFamily Join()
    {
        var families = new Families(_account, _store, _accounts, _api, _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), _time));
        var bring = new BringFamilyEntriesOver(_sessions, _account, _moves, _records, _store, _api, Substitute.For<IVersionClock>(), families);
        return new JoinFamily(_store, _api, _sessions, _account, new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>()), _moves,
            _households, _syncStore, _sync, families, bring, _time);
    }

    private Task SignInAsync(bool joinPending = false, HouseholdId? moveFrom = null) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, joinPending, PendingMoveFrom: moveFrom), CancellationToken.None);

    private async Task SignOutAndInAsync()
    {
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync();
    }

    private async Task<AcceptedInvitation> SignOutAndInThenRefuseAsync(string code)
    {
        await SignOutAndInAsync();
        throw new HouseholdRefusedException(code);
    }

    private void Pending(DateTimeOffset? at = null) =>
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(Code, at ?? Now));

    private void Joining(HouseholdId source) =>
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, source, "Andrzej"));

    /// <summary>The joined family is pulled; the source has synced and is her family, owned alone, with entries to offer.</summary>
    private void SyncedSourceWithEntries()
    {
        Joining(Home);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));
    }

    [Theory]
    [InlineData("abcd-efgh")]
    [InlineData(" ABCD EFGH ")]
    [InlineData("https://api.coo.lechdigital.nl/i/ABCDEFGH")]
    [InlineData("https://api.coo.lechdigital.nl/i/abcd-efgh")]
    public async Task ReceiveAsync_PastedLink_StoresTheCode(string text)
    {
        // Act
        var received = await Join().ReceiveAsync(text, CancellationToken.None);

        // Assert
        received.Should().BeTrue();
        await _store.Received(1).SetPendingInvitationAsync(new PendingInvitation(Code, Now), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ABCD-EFG")]
    [InlineData("https://example.com/i/ABCDEFGH")]
    [InlineData("https://api.coo.lechdigital.nl/privacy/ABCDEFGH")]
    [InlineData("https://api.coo.lechdigital.nl/abABCDEFGH")]
    [InlineData("Join my family on Coo")]
    [InlineData(null)]
    public async Task ReceiveAsync_Garbage_StoresNothing(string? text)
    {
        // Act
        var received = await Join().ReceiveAsync(text, CancellationToken.None);

        // Assert
        received.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _api.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Fact]
    public async Task NeedsSignInAsync_SignedOutThenIn_SaysWhetherTheSignInStepOpens()
    {
        // Act
        var before = await Join().NeedsSignInAsync(CancellationToken.None);
        await SignInAsync();
        var after = await Join().NeedsSignInAsync(CancellationToken.None);

        // Assert
        before.Should().BeTrue();
        after.Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_PendingOlderThanSeventyTwoHours_DropsIt()
    {
        // Arrange
        Pending(Now.AddHours(-72));

        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.None);
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StageAsync_PendingYoungerThanSeventyTwoHours_ReopensTheInvitation()
    {
        // Arrange
        Pending(Now.AddHours(-71));

        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.Invitation);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
    }

    [Fact]
    public async Task StageAsync_NothingStored_OpensNothing()
    {
        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.None);
    }

    [Fact]
    public async Task StageAsync_JoiningMarkerStored_ReopensAtTheChoice()
    {
        // Arrange: Review Focus 2, the app was closed between Join and the choice.
        Joining(Home);
        Pending();

        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.Choice);
    }

    [Fact]
    public async Task PreviewAsync_NothingPending_SaysSo()
    {
        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(PreviewStatus.NothingPending);
        await _api.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Fact]
    public async Task PreviewAsync_Invitation_IsReadyAndKeepsThePendingInvitation()
    {
        // Arrange
        await SignInAsync();
        Pending();
        var invitation = new InvitationPreview(Joined, "Andrzej", ["Maja"], Role.Parent, Now.AddHours(72), false);
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(invitation);

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Should().Be(new JoinPreview(PreviewStatus.Ready, invitation));
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
    }

    [Fact]
    public async Task PreviewAsync_AlreadyAMember_ClearsThePendingInvitationAndShowsTheFamily()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Maja"], Role.Parent, Now.AddHours(72), true));

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(PreviewStatus.AlreadyMember);
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        _sync.Received(1).NotifyDataChanged();
    }

    [Fact]
    public async Task PreviewAsync_AlreadyAMemberOfAFamilyNotOnThePhoneYet_ShowsNothingNew()
    {
        // Arrange: a family without settings on the phone is not activated (it would get a copy of the shown one's).
        await SignInAsync();
        Pending();
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home)]);
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", [], Role.Parent, Now.AddHours(72), true));

        // Act
        await Join().PreviewAsync(CancellationToken.None);

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Theory]
    [InlineData(HouseholdErrors.InvitationInvalid, PreviewStatus.Invalid, true)]
    [InlineData(HouseholdErrors.RateLimited, PreviewStatus.RateLimited, false)]
    [InlineData(HouseholdErrors.Malformed, PreviewStatus.Unavailable, false)]
    public async Task PreviewAsync_Refused_MapsTheCode(string code, PreviewStatus expected, bool cleared)
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(code));

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(expected);
        await _store.Received(cleared ? 1 : 0).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewAsync_NoAnswer_KeepsThePendingInvitation()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(PreviewStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewAsync_SignedOutAndInWhileAsking_WritesNothing(bool alreadyMember)
    {
        // Arrange: the answer belongs to the old sign-in (the Increment 11 generation rule).
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SignOutAndInAsync();
            return alreadyMember
                ? new InvitationPreview(Joined, "Andrzej", [], Role.Parent, Now.AddHours(72), true)
                : throw new HouseholdRefusedException(HouseholdErrors.InvitationInvalid);
        });

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(PreviewStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Theory]
    [InlineData(ConsentState.Given, true)]
    [InlineData(ConsentState.Declined, false)]
    public async Task HasConsentAsync_CachedConsent_SaysWhetherTheSheetIsNeeded(ConsentState cached, bool expected)
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(cached);

        // Act
        var given = await Join().HasConsentAsync(CancellationToken.None);

        // Assert
        given.Should().Be(expected);
    }

    [Fact]
    public async Task HasConsentAsync_SignedOut_IsFalse()
    {
        // Act
        var given = await Join().HasConsentAsync(CancellationToken.None);

        // Assert
        given.Should().BeFalse();
    }

    [Fact]
    public async Task AcceptAsync_NothingPending_AcceptsNothing()
    {
        // Arrange
        await SignInAsync();

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.NothingPending);
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
    }

    [Fact]
    public async Task AcceptAsync_SignedOut_AcceptsNothing()
    {
        // Arrange
        Pending();

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Unavailable);
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
    }

    [Theory]
    [InlineData(AcceptOutcome.Joined)]
    [InlineData(AcceptOutcome.Rejoined)]
    public async Task AcceptAsync_Joined_RecordsTheSourceRefreshesAndPullsTheFamily(AcceptOutcome outcome)
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, outcome));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert: the marker is stored before the pending invitation goes, so the mid-join guard never lapses (§5.3).
        accepted.Status.Should().Be(AcceptStatus.Joined);
        Received.InOrder(() =>
        {
            _store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej"), Arg.Any<CancellationToken>());
            _store.SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
            _accounts.GetMeAsync(Arg.Any<CancellationToken>());
            _sync.SyncNowAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AcceptAsync_CancelledAfterTheServerAccepted_StillStoresTheJoin()
    {
        // Arrange: nobody becomes a member in limbo: once the server said joined, the marker is stored whatever the token says.
        await SignInAsync();
        Pending();
        using var cancel = new CancellationTokenSource();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancel.Cancel();
            return new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined);
        });
        _store.SetJoiningAsync(Arg.Any<JoiningMarker?>(), Arg.Is<CancellationToken>(t => t.IsCancellationRequested)).ThrowsAsync(new OperationCanceledException());
        _store.SetPendingInvitationAsync(Arg.Any<PendingInvitation?>(), Arg.Is<CancellationToken>(t => t.IsCancellationRequested))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        Func<Task> act = async () => await Join().AcceptAsync("Andrzej", cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        await _store.Received(1).SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej"), CancellationToken.None);
        await _store.Received(1).SetPendingInvitationAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task AcceptAsync_AlreadyMember_ClearsThePendingInvitationAndShowsTheFamilyWithoutAChoice()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.AlreadyMember));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.AlreadyMember);
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task AcceptAsync_NoAnswer_KeepsThePendingInvitation()
    {
        // Arrange: spec §5.2, "Try again".
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Theory]
    [InlineData(HouseholdErrors.InvitationInvalid, AcceptStatus.Invalid, true)]
    [InlineData(HouseholdErrors.RateLimited, AcceptStatus.RateLimited, false)]
    [InlineData(HouseholdErrors.Forbidden, AcceptStatus.Unavailable, false)]
    public async Task AcceptAsync_Refused_MapsTheCodeAndJoinsNothing(string code, AcceptStatus expected, bool cleared)
    {
        // Arrange: ruling 25.
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(code));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(expected);
        await _store.Received(cleared ? 1 : 0).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task AcceptAsync_ServerSaysConsentRequired_ForgetsTheCachedConsentAndAsksForIt()
    {
        // Arrange: §5.1, consent before membership. The invitation stays pending so the accept follows the consent sheet.
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(SyncErrors.ConsentRequired));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.NeedsConsent);
        await _state.Received(1).SetConsentAsync(Me, ConsentState.Unknown, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Theory]
    [InlineData(AcceptOutcome.Joined)]
    [InlineData(AcceptOutcome.AlreadyMember)]
    public async Task AcceptAsync_SignedOutAndInWhileAccepting_WritesNothing(AcceptOutcome outcome)
    {
        // Arrange: the answer belongs to the old sign-in; the invitation stays pending and a retry is idempotent (§5.2).
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SignOutAndInAsync();
            return new AcceptedInvitation(Joined, Role.Parent, outcome);
        });

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Theory]
    [InlineData(HouseholdErrors.InvitationInvalid)]
    [InlineData(SyncErrors.ConsentRequired)]
    public async Task AcceptAsync_SignedOutAndInBeforeARefusal_WritesNothing(string code)
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(_ => SignOutAndInThenRefuseAsync(code));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _state.DidNotReceiveWithAnyArgs().SetConsentAsync(default, default, default);
    }

    [Fact]
    public async Task DecideAsync_NoJoinInProgress_AsksNothing()
    {
        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _sync.DidNotReceiveWithAnyArgs().SyncNowAsync(default);
    }

    [Fact]
    public async Task DecideAsync_SourceIsTheJoinedFamily_AsksNothingAndFinishes()
    {
        // Arrange: ruling 2, the Pixel held X from its first account.
        await SignInAsync(moveFrom: Local);
        Joining(Joined);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _store.Received(1).SetJoiningAsync(null, Arg.Any<CancellationToken>());
        _sessions.Stored!.PendingMoveFrom.Should().BeNull("ruling 4: the invitation's choice replaced the created move");
        _sync.Received(1).NotifyDataChanged();
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task DecideAsync_SourceOfAnotherAccount_AsksNothing()
    {
        // Arrange: ruling 3, synced but not one of the account's families.
        await SignInAsync();
        Joining(Local);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Local, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().SummaryAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_SourceWithEntries_AsksAndSaysWhetherItCopies()
    {
        // Arrange
        await SignInAsync();
        SyncedSourceWithEntries();

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.Ask);
        choice.InviterName.Should().Be("Andrzej");
        choice.Copies.Should().BeTrue();
        choice.Source.Should().Be(Home);
        choice.Offer!.Local.EntryCount.Should().Be(42);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_NoEntriesButABabyAndTheTargetHasNone_AsksToBringTheBaby()
    {
        // Arrange: Spec 011's M7 rule.
        await SignInAsync();
        Joining(Home);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.Ask);
        choice.Copies.Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_EmptyNeverSyncedAdoptedFamily_AsksNothingAndLeavesIt()
    {
        // Arrange: §6, "an empty source that is her account's never-synced family is still left as in §6.1".
        await SignInAsync();
        Joining(Home);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _api.Received(1).LeaveAsync(Home, false, Arg.Any<CancellationToken>());
        await _store.Received(1).SetJoiningAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_EmptySyncedFamily_AsksNothingAndLeavesNothing()
    {
        // Arrange: a family that synced is never left without asking (§6.2).
        await SignInAsync();
        Joining(Home);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task DecideAsync_JoinedFamilyNotPulledYet_IsNotReadyAndPullsAgain()
    {
        // Arrange
        await SignInAsync();
        Joining(Home);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NotReady);
        await _sync.Received(1).SyncNowAsync(Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_PulledByTheCycleItRuns_Decides()
    {
        // Arrange
        await SignInAsync();
        SyncedSourceWithEntries();
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false, true);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.Ask);
        await _sync.Received(1).SyncNowAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_SignedOutAndInWhilePulling_FinishesNothing()
    {
        // Arrange: the cycle's wait spans a sign-in; the old sign-in's join is not finished under the new one.
        await SignInAsync();
        Joining(Joined);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false, true);
        _sync.SyncNowAsync(Arg.Any<CancellationToken>()).Returns(_ => SignOutAndInAsync());

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NotReady);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_ShowingTheFamilyFails_StillClearsTheJoiningMarker()
    {
        // Arrange: the choice is made; the mid-join guard must not outlive it whatever happens next.
        await SignInAsync();
        Joining(Joined);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.ActivateAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        // Act
        Func<Task> act = async () => await Join().DecideAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        await _store.Received(1).SetJoiningAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task BringAsync_Committed_FinishesTheJoin()
    {
        // Arrange
        await SignInAsync(joinPending: true);
        SyncedSourceWithEntries();
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(42, 0));

        // Act
        var outcome = await Join().BringAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.Copied, 42, 0, OldFamilyQuestion.DeleteOld));
        await _store.Received(1).SetJoiningAsync(null, Arg.Any<CancellationToken>());
        _account.State.Should().BeOfType<SignedIn>();
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task BringAsync_FailsBeforeCommitting_KeepsTheChoiceOpen()
    {
        // Arrange: nothing was written, so the choice is offered again (Try again, or Keep them separate).
        await SignInAsync();
        SyncedSourceWithEntries();
        _records.CopyAsync(default!, default, default, default, default).ThrowsAsyncForAnyArgs(new IOException("disk"));

        // Act
        Func<Task> act = async () => await Join().BringAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task BringAsync_SignedOutAndInWhileLeavingTheOldFamily_FinishesNothing()
    {
        // Arrange: the move committed; the leave's answer arrives under a new sign-in, which this join does not finish.
        await SignInAsync();
        Joining(Home);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(true);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]), new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));
        _api.LeaveAsync(Home, false, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await SignOutAndInAsync();
            return LeaveOutcome.Deleted;
        });

        // Act
        var outcome = await Join().BringAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task BringAsync_NoJoinInProgress_BringsNothing()
    {
        // Arrange
        await SignInAsync();

        // Act
        var outcome = await Join().BringAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.AlreadyCopied);
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task KeepSeparateAsync_JoinPendingSession_CompletesTheJoinWithoutABringOver()
    {
        // Arrange: §5.3, the invitation's choice replaces Spec 011's join-pending choice.
        await SignInAsync(joinPending: true);
        Joining(Local);

        // Act
        await Join().KeepSeparateAsync(CancellationToken.None);

        // Assert
        _account.State.Should().BeOfType<SignedIn>();
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        await _store.Received(1).SetJoiningAsync(null, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task KeepSeparateAsync_NoJoinInProgress_ChangesNothing()
    {
        // Arrange
        await SignInAsync();

        // Act
        await Join().KeepSeparateAsync(CancellationToken.None);

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
        _sync.DidNotReceive().RequestSync();
    }

    [Fact]
    public async Task DeclineAsync_PendingInvitation_ClearsItAndAsksTheServerNothing()
    {
        // Arrange
        Pending();

        // Act
        await Join().DeclineAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        _api.ReceivedCalls().Should().BeEmpty();
    }
}
