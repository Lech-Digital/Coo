using Coo.Contracts.Households;
using Coo.Contracts.Sync;
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

/// <summary>Spec 011b §5–§6 as the person sees them: each step, each answer, each Not now.</summary>
[Collection(nameof(StringsState))]
public sealed class JoinViewModelTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;
    private static readonly BabyId MyLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId TheirLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncStore _syncStore = Substitute.For<ISyncStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IConsentPrompt _consent = Substitute.For<IConsentPrompt>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly IVersionClock _versions = Substitute.For<IVersionClock>();
    private readonly List<Families> _made = [];
    private readonly IAuthApi _auth = Substitute.For<IAuthApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();

    public JoinViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _clock.Is24Hour.Returns(true);
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(Code, Now));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Home);
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Lena"], Role.Parent, Now.AddHours(72), false));
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        _versions.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 0, Phone));
        _households.ListBabiesAsync(Home, Arg.Any<CancellationToken>()).Returns([Baby(Home, "Lena")]);
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

    private static BabyProfile Baby(HouseholdId household, string name) =>
        BabyProfile.Create(MyLena, household, name, [], new DateOnly(2026, 8, 20), null, new HlcStamp(1_790_000_000_000, 0, Phone), new DateOnly(2026, 10, 6)).Value;

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("a", Now, "r", new AccountProfile(Me, "Kasia", "k@example.com"), Home, JoinPending: false), CancellationToken.None);

    private JoinViewModel Join()
    {
        var families = new Families(_account, _store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(_versions, Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        _made.Add(families);
        var sync = SyncControls.PassThrough();
        var bring = new BringFamilyEntriesOver(_sessions, _account, _moves, _records, _store, _api, _versions, families, sync);
        var join = new JoinFamily(_store, _api, _sessions, _account, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()),
            _moves, _households, _syncStore, sync, families, bring, new FakeTimeProvider(Now));
        var signIn = new SignIn(_auth, _google, _households, _device,
            Substitute.For<IDeviceName>(), _account);
        return new JoinViewModel(join, signIn, _consent, _clock, new FamilyBringOverViewModel(new LeaveFamily(_api, families, sync, _account)));
    }

    [Fact]
    public async Task StartAsync_SignedOut_AsksToSignInFirst()
    {
        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.SignIn);
        JoinViewModel.SignInTitle.Should().Be("Sign in to join a family");
        model.CodeLine.Should().Be("Invitation code: ABCD-EFGH");
    }

    [Fact]
    public async Task StartAsync_SignedIn_ShowsTheInvitationWithTheInviterAndTheBaby()
    {
        // Arrange
        await SignInAsync();

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Invitation);
        model.Title.Should().Be("Join Andrzej's family?");
        model.Body.Should().Be("Andrzej invited you to join as a parent. You'll both see the family's log and add to it.");
        model.Babies.Should().Be("Their family's log has: Lena.");
    }

    [Fact]
    public async Task JoinCommand_ConsentRefused_JoinsNothingAndSaysWhenItCanStillBeUsed()
    {
        // Arrange: §5.1 step 3.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(ConsentAnswer.Declined);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().StartWith("You can join later with the same link, until ");
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinCommand_NothingToBring_FinishesWithTheJoinedToast()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, new JoiningMarker(Joined, Home, "Andrzej"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_NoAnswer_OffersTryAgainAndKeepsTheInvitation()
    {
        // Arrange: §5.2.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("That didn't work. Check your connection and try again.");
        model.CanRetry.Should().BeTrue();
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_InvalidCode_SaysItCannotBeUsed()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationInvalid));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Message.Should().Be("This invitation can't be used. It may have expired, been used or been withdrawn. Ask for a new one.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_SyncedSourceWithEntries_AsksThenCopiesThenDeletesTheOldFamilyByItself()
    {
        // Arrange: §6.2 end to end in the view models; the old family goes without a second question (owner, 2026-11-03).
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, new JoiningMarker(Joined, Home, "Andrzej"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));
        var model = Join();
        string? toast = null;
        model.BringOver.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);
        var summary = model.BringOver.Summary;
        await model.BringOver.BringCommand.ExecuteAsync(null);
        await model.BringOver.ConfirmCommand.ExecuteAsync(null);

        // Assert
        summary.Should().Be("This phone has 42 entries for Lena.");
        await _api.Received(1).LeaveAsync(Home, true, Arg.Any<CancellationToken>());
        toast.Should().Be("Your entries are now in Andrzej's family, and your old family is gone.");
    }

    [Fact]
    public async Task JoinCommand_OldFamilyCannotBeDeletedOffline_AsksAsBefore()
    {
        // Arrange: offline after the copy: the old family stays, and the question lets her try again.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, new JoiningMarker(Joined, Home, "Andrzej"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        _records.CopyAsync(default!, default, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));
        _api.LeaveAsync(Home, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);
        await model.BringOver.BringCommand.ExecuteAsync(null);
        await model.BringOver.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Choice);
        model.BringOver.Step.Should().Be(BringOverStep.OldFamily);
        model.BringOver.Question.Should().Be("Your entries are now in Andrzej's family too. Delete your old family? Its 42 entries for Lena are deleted from Mochi's server and this phone. The copies stay.");
        model.BringOver.QuestionNote.Should().Be("2 entries can't be brought over and are deleted with it.");
        model.BringOver.AnswerText.Should().Be("Delete old family");
    }

    [Fact]
    public async Task ContinueCommand_EnterAMalformedCode_SaysHowACodeLooksAndSendsNothing()
    {
        // Arrange
        var model = new EnterCodeViewModel(new JoinFamily(_store, _api, _sessions, _account,
            new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _moves, _households, _syncStore,
            Substitute.For<ISyncControl>(), null!, null!, new FakeTimeProvider(Now)))
        { Code = "ABCD-EFG" };
        var accepted = 0;
        model.Accepted += (_, _) => accepted++;

        // Act
        await model.ContinueCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().Be("Check the code: it's 8 letters and numbers, like ABCD-EFGH.");
        accepted.Should().Be(0);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
    }

    private static readonly JoiningMarker Marker = new(Joined, Home, "Andrzej");

    /// <summary>The phone has the joined family's log and a synced source with entries: §6.2's question will be asked.</summary>
    private void AfterAcceptWithEntries()
    {
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
    }

    private void Granted() =>
        _auth.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).Returns(new SignInGrant(
            new SessionTokens("a", Now.AddMinutes(15), "r"), new AccountProfile(Me, "Kasia", "k@example.com"), Home, SignInOutcome.Existing));

    [Fact]
    public async Task SignInCommand_SignInSucceeds_ShowsTheInvitation()
    {
        // Arrange
        _google.GetIdTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("token");
        Granted();
        var model = Join();
        await model.StartAsync();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Invitation);
        model.Title.Should().Be("Join Andrzej's family?");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task SignInCommand_SignInFails_SaysSoAndStaysOnSignIn()
    {
        // Arrange
        _auth.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException());
        var model = Join();
        await model.StartAsync();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.SignIn);
        model.Toast.Should().Be("Sign-in didn't work. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task SignInCommand_PersonClosesGoogleSheet_SaysNothingAndStaysOnSignIn()
    {
        // Arrange
        _google.GetIdTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.SignIn);
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInCommand_SignInThrowsSomethingUnexpected_ShowsTheToastAndNeverEscapes()
    {
        // Arrange
        _auth.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("Sign-in didn't work. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_NothingPending_FinishesWithoutAToast()
    {
        // Arrange
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns((PendingInvitation?)null);
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;

        // Act
        await model.StartAsync();

        // Assert
        toast.Should().Be("");
    }

    [Fact]
    public async Task StartAsync_StoreThrows_OffersTryAgainAndRecovers()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        var model = Join();

        // Act
        await model.StartAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns((JoiningMarker?)null);
        var failedStep = model.Step;
        var failedMessage = model.Message;
        var canRetry = model.CanRetry;
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        failedStep.Should().Be(JoinStep.Message);
        failedMessage.Should().Be("That didn't work. Check your connection and try again.");
        canRetry.Should().BeTrue();
        model.Step.Should().Be(JoinStep.Invitation);
    }

    [Fact]
    public async Task NotNowCommand_OnTheInvitation_ForgetsTheCodeAndFinishesWithoutAToast()
    {
        // Arrange
        await SignInAsync();
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
        toast.Should().Be("");
    }

    [Fact]
    public async Task NotNowCommand_ForgettingFails_StillFinishes()
    {
        // Arrange
        await SignInAsync();
        var model = Join();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        await model.StartAsync();
        _store.SetPendingInvitationAsync(null, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        finished.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_PreviewSaysAlreadyMember_SaysSoWithoutAChoice()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Lena"], Role.Parent, Now.AddHours(72), true));
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.AlreadyMember));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("You're already in this family.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_PreviewRateLimited_SaysToWaitWithoutTryAgain()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.RateLimited));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Message.Should().Be("Too many tries. Wait a while and try again.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task RetryCommand_PreviewWasUnavailable_ShowsTheInvitationOnceTheServerAnswers()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        var model = Join();
        await model.StartAsync();
        var offered = model.CanRetry;
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", [], Role.Parent, Now.AddHours(72), false));

        // Act
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        offered.Should().BeTrue();
        model.Step.Should().Be(JoinStep.Invitation);
        model.Babies.Should().Be("Their family's log has no baby yet.");
    }

    [Fact]
    public async Task RetryCommand_RetryFailsAgain_KeepsTheMessageAndSaysSo()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        var model = Join();
        await model.StartAsync();
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        // Act
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_AcceptSaysAlreadyMember_SaysSoWithoutAChoice()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.AlreadyMember));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Message.Should().Be("You're already in this family.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_AcceptSaysInvalid_SaysTheSameThingAsAnInvalidPreview()
    {
        // Arrange: one message for every cause, so the code reveals nothing.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationInvalid));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Message.Should().Be("This invitation can't be used. It may have expired, been used or been withdrawn. Ask for a new one.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_AcceptRateLimited_SaysToWait()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.RateLimited));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Message.Should().Be("Too many tries. Wait a while and try again.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_AcceptThrowsSomethingUnexpected_ShowsTheToastAndStaysOnTheInvitation()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Invitation);
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task RetryCommand_AcceptWasUnavailable_AcceptsAgain()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(
            _ => throw new HouseholdUnavailableException("offline"),
            _ => new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined));
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();
        await model.JoinCommand.ExecuteAsync(null);

        // Act
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        await _api.Received(2).AcceptAsync(Code, Arg.Any<CancellationToken>());
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_ConsentSheetCouldNotOpen_KeepsTheInvitationAndOffersTryAgain()
    {
        // Arrange: review I1, "couldn't ask" is not "said no".
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(ConsentAnswer.NotAsked);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("That didn't work. Check your connection and try again.");
        model.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task RetryCommand_AfterTheConsentSheetCouldNotOpen_AsksAgainAndJoins()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(ConsentAnswer.NotAsked, ConsentAnswer.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();
        await model.JoinCommand.ExecuteAsync(null);

        // Act
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        await _consent.Received(2).AskAsync();
        await _api.Received(1).AcceptAsync(Code, Arg.Any<CancellationToken>());
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_ServerWantsConsentAndTheSheetCouldNotOpen_KeepsTheInvitationAndOffersTryAgain()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(SyncErrors.ConsentRequired));
        _consent.AskAsync().Returns(ConsentAnswer.NotAsked);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        model.Message.Should().Be("That didn't work. Check your connection and try again.");
        model.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task JoinCommand_ALinkReplacedTheCodeSinceThePreview_ShowsTheNewInvitationAndAcceptsNothing()
    {
        // Arrange: review I2, the accept must be for the invitation the person saw.
        await SignInAsync();
        var other = InvitationCode.Parse("ZZZZZZZZ").Value;
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(
            new PendingInvitation(Code, Now), new PendingInvitation(Code, Now), new PendingInvitation(other, Now));
        _api.PreviewAsync(other, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Home, "Ola", [], Role.Parent, Now.AddHours(72), false));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
        model.Step.Should().Be(JoinStep.Invitation);
        model.Title.Should().Be("Join Ola's family?");
    }

    [Fact]
    public async Task JoinCommand_ServerWantsConsent_AsksThenAcceptsAgain()
    {
        // Arrange: §5.1, the phone thought consent was given but the server disagrees.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(
            _ => throw new HouseholdRefusedException(SyncErrors.ConsentRequired),
            _ => new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined));
        _consent.AskAsync().Returns(ConsentAnswer.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _consent.Received(1).AskAsync();
        await _api.Received(2).AcceptAsync(Code, Arg.Any<CancellationToken>());
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_ServerWantsConsentAndPersonRefuses_JoinsNothingAndSaysUntilWhen()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(SyncErrors.ConsentRequired));
        _consent.AskAsync().Returns(ConsentAnswer.Declined);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _api.Received(1).AcceptAsync(Code, Arg.Any<CancellationToken>());
        model.Message.Should().StartWith("You can join later with the same link, until ");
        await _store.DidNotReceive().SetJoiningAsync(Arg.Is<JoiningMarker?>(m => m != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_JoiningMarkerStored_OpensAtTheChoice()
    {
        // Arrange: the app was closed after Join (§5.3).
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Choice);
        model.BringOver.Step.Should().Be(BringOverStep.Choice);
        model.BringOver.FamilyLine.Should().Be("Their family's log has: Lena.");
        model.BringOver.CanKeep.Should().BeTrue();
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
    }

    [Fact]
    public async Task StartAsync_JoinedFamilyNotOnThePhoneYet_OffersTryAgainAndKeepSeparate()
    {
        // Arrange: Task 16 carry, the person is never stuck.
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("Couldn't refresh. Check your connection.");
        model.CanRetry.Should().BeTrue();
        model.CanKeepSeparate.Should().BeTrue();
    }

    [Fact]
    public async Task KeepSeparateCommand_JoinedFamilyNotOnThePhoneYet_ClearsTheMarkerAndFinishesWithTheJoinedToast()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.KeepSeparateCommand.ExecuteAsync(null);

        // Assert
        await _store.Received().SetJoiningAsync(null, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task KeepSeparateCommand_ClearingFails_ShowsTheToastAndKeepsTheOffer()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);
        var model = Join();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        await model.StartAsync();
        _store.SetJoiningAsync(null, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        // Act
        await model.KeepSeparateCommand.ExecuteAsync(null);

        // Assert
        finished.Should().Be(0);
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        model.CanKeepSeparate.Should().BeTrue();
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task RetryCommand_JoinedFamilyArrives_OpensTheChoiceAndDropsKeepSeparate()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false, false, true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Choice);
        model.CanKeepSeparate.Should().BeFalse();
    }

    [Fact]
    public async Task OkCommand_AfterAMessage_FinishesWithoutAToast()
    {
        // Arrange
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.ShowMalformed();

        // Act
        model.OkCommand.Execute(null);

        // Assert
        toast.Should().Be("");
    }

    [Fact]
    public void ShowMalformed_ALinkWithoutACode_SaysTheSameAsAnInvalidInvitation()
    {
        // Act
        var model = Join();
        model.ShowMalformed();

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("This invitation can't be used. It may have expired, been used or been withdrawn. Ask for a new one.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task KeepCommand_OnTheChoice_FinishesWithTheBothFamiliesToast()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();
        await model.JoinCommand.ExecuteAsync(null);

        // Act
        await model.BringOver.KeepCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("Both families are on this phone. Switch between them in Settings → Family.");
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ContinueCommand_ACodeOrALink_BecomesThePendingInvitationAndAccepts()
    {
        // Arrange
        var model = EnterCode();
        model.Code = "abcd-efgh";
        var accepted = 0;
        model.Accepted += (_, _) => accepted++;

        // Act
        await model.ContinueCommand.ExecuteAsync(null);

        // Assert
        accepted.Should().Be(1);
        model.Error.Should().BeEmpty();
        await _store.Received(1).SetPendingInvitationAsync(Arg.Is<PendingInvitation?>(p => p!.Code == Code), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinueCommand_ACorrectedCodeAfterAMalformedOne_ClearsTheError()
    {
        // Arrange
        var model = EnterCode();
        model.Code = "ABCD";
        await model.ContinueCommand.ExecuteAsync(null);
        model.Code = "ABCDEFGH";

        // Act
        await model.ContinueCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().BeEmpty();
    }

    [Fact]
    public async Task ContinueCommand_StoringTheCodeFails_SaysItDidNotWorkAndNeverEscapes()
    {
        // Arrange
        _store.SetPendingInvitationAsync(Arg.Any<PendingInvitation?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        var model = EnterCode();
        model.Code = "ABCDEFGH";
        var accepted = 0;
        model.Accepted += (_, _) => accepted++;

        // Act
        await model.ContinueCommand.ExecuteAsync(null);

        // Assert
        accepted.Should().Be(0);
        model.Error.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task JoinCommand_NoConsentPersonAgrees_AcceptsAndFinishesWithTheJoinedToast()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(ConsentAnswer.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _consent.Received(1).AskAsync();
        await _api.Received(1).AcceptAsync(Code, Arg.Any<CancellationToken>());
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_PersonJustAgreedAndServerStillWantsConsent_DoesNotAskASecondTime()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(ConsentAnswer.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(SyncErrors.ConsentRequired));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _consent.Received(1).AskAsync();
        model.Message.Should().StartWith("You can join later with the same link, until ");
    }

    [Fact]
    public async Task NotNowCommand_WhileJoining_DoesNothing()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        var gate = new TaskCompletionSource<AcceptedInvitation>();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var model = Join();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        await model.StartAsync();
        var joining = model.JoinCommand.ExecuteAsync(null);

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        finished.Should().Be(0);
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        gate.SetException(new HouseholdUnavailableException("offline"));
        await joining;
    }

    [Fact]
    public async Task SignInCommand_PreviewFailsUnexpectedlyAfterSigningIn_SaysItDidNotWorkNotThatSignInFailed()
    {
        // Arrange
        _google.GetIdTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("token");
        Granted();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task JoinCommand_FailsAfterTheAccept_OffersTryAgainAtTheChoice()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);
        var message = model.Message;
        var canRetry = model.CanRetry;
        var canKeep = model.CanKeepSeparate;
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        message.Should().Be("That didn't work. Check your connection and try again.");
        canRetry.Should().BeTrue();
        canKeep.Should().BeFalse("a failed choice can't tell whether this phone has a baby, so it offers Try again only");
        model.Step.Should().Be(JoinStep.Choice);
    }

    [Fact]
    public async Task StartAsync_PreviewSaysJoinedAfterALostAcceptAnswer_OpensTheChoice()
    {
        // Arrange: final review C2, the accept's answer was lost; the preview says already a member and the retried accept joined.
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Lena"], Role.Parent, Now.AddHours(72), true));
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyLena, "Lena")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirLena, "Lena")]));
        var finished = 0;

        // Act
        var model = Join();
        model.Finished += (_, _) => finished++;
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Choice);
        model.BringOver.Summary.Should().Be("This phone has 42 entries for Lena.");
        finished.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_PreviewSaysJoinedAndTheChoiceFails_OffersTryAgainOnly()
    {
        // Arrange: the join is stored, so a failed choice is not a failed join.
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Lena"], Role.Parent, Now.AddHours(72), true));
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("That didn't work. Check your connection and try again.");
        model.CanRetry.Should().BeTrue();
        model.CanKeepSeparate.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_ALinkReplacedTheCodeAndItsPreviewThrows_OffersTryAgainThatStartsOver()
    {
        // Arrange: final review minor, the new invitation could not be shown; the sheet must not stay on the old one.
        await SignInAsync();
        var other = InvitationCode.Parse("ZZZZZZZZ").Value;
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(
            new PendingInvitation(Code, Now), new PendingInvitation(Code, Now), new PendingInvitation(other, Now));
        _api.PreviewAsync(other, Arg.Any<CancellationToken>()).Returns(
            _ => throw new InvalidOperationException("disk"),
            _ => new InvitationPreview(Home, "Ola", [], Role.Parent, Now.AddHours(72), false));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);
        var step = model.Step;
        var message = model.Message;
        var canRetry = model.CanRetry;
        await model.RetryCommand.ExecuteAsync(null);

        // Assert
        step.Should().Be(JoinStep.Message);
        message.Should().Be("That didn't work. Check your connection and try again.");
        canRetry.Should().BeTrue();
        model.Step.Should().Be(JoinStep.Invitation);
        model.Title.Should().Be("Join Ola's family?");
    }

    [Fact]
    public async Task NotNowCommand_AfterALinkWithoutACode_KeepsTheInvitationThatWasPending()
    {
        // Arrange: final review minor, Back on the malformed-link message is about that link only.
        await SignInAsync();
        var model = Join();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        model.ShowMalformed();

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        finished.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_JoinedFamilyNotOnThePhoneYetAndNoBabyYet_OffersTryAgainButNotKeepSeparate()
    {
        // Arrange: final review minor, setup shows; keeping them separate would have the person add a second baby.
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(Marker);
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);
        _households.ListBabiesAsync(Home, Arg.Any<CancellationToken>()).Returns([]);

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.CanRetry.Should().BeTrue();
        model.CanKeepSeparate.Should().BeFalse();
    }

    private EnterCodeViewModel EnterCode() => new(new JoinFamily(_store, _api, _sessions, _account,
        new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _moves, _households, _syncStore,
        Substitute.For<ISyncControl>(), null!, null!, new FakeTimeProvider(Now)));
}
