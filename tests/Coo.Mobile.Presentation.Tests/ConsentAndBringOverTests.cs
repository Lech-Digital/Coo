using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §3 (the consent sheet) and §4 (bring my entries over, keep them separate).</summary>
[Collection(nameof(StringsState))]
public sealed class ConsentAndBringOverTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly HouseholdId Mine = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId MyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId FamilyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly ISessionStore _sessions = Substitute.For<ISessionStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IConsentApi _consentApi = Substitute.For<IConsentApi>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IAboutInfo _about = Substitute.For<IAboutInfo>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();

    public ConsentAndBringOverTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _sessions.GetAsync(Arg.Any<CancellationToken>()).Returns(new StoredSession("a", DateTimeOffset.UnixEpoch, "r", Profile, Family, JoinPending: true));
        _about.Informational.Returns("0.11.0+abcdef1234");
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Mine);
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Family, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new LocalBaby(FamilyMaja, "Maja")]));
        _state.GetConsentAsync(Profile.UserId, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.HasPulledAsync(Family, Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private ConsentViewModel Consent()
    {
        var gate = new ConsentGate(_state, Substitute.For<IAccountApi>(), _consentApi);
        return new ConsentViewModel(new RecordConsent(_sessions, gate, _sync), new DeclineConsent(_sessions, gate, _sync), _launcher, _about);
    }

    private BringOverViewModel BringOver(Account account) => new(new BringEntriesOver(
        _sessions, _moves, _store, new ConsentGate(_state, Substitute.For<IAccountApi>(), _consentApi), account, _sync));

    [Fact]
    public void Texts_English_AreTheSpecsWording()
    {
        // Assert
        ConsentViewModel.Title.Should().Be("Share your log with your family?");
        ConsentViewModel.Body.Should().StartWith("To share, Coo keeps your baby's care log (feeds, diapers, sleep and pumping");
        ConsentViewModel.Body2.Should().EndWith("The log on this phone always stays here.");
        ConsentViewModel.AgreeText.Should().Be("Agree and share");
        ConsentViewModel.NotNowText.Should().Be("Not now");
    }

    [Fact]
    public async Task AgreeCommand_Recorded_SendsTheVersionAndLanguageAndCloses()
    {
        // Arrange
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.AgreeCommand.ExecuteAsync(null);

        // Assert
        await _consentApi.Received(1).RecordAsync("0.11.0", "en", Arg.Any<CancellationToken>());
        closed.Should().Be(1);
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task AgreeCommand_Offline_SaysSoAndStaysOpen()
    {
        // Arrange
        _consentApi.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.AgreeCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(0);
        model.Toast.Should().Be("Sharing couldn't be turned on. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task NotNowCommand_Declines_SendsNothingAndCloses()
    {
        // Arrange
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        await _state.Received(1).SetConsentAsync(Profile.UserId, ConsentState.Declined, Arg.Any<CancellationToken>());
        await _consentApi.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
        closed.Should().Be(1);
    }

    [Fact]
    public async Task OpenPrivacyCommand_OpensThePolicy()
    {
        // Act
        await Consent().OpenPrivacyCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).OpenBrowserAsync(SettingsViewModel.PrivacyUrl);
    }

    [Fact]
    public async Task LoadAsync_JoinPending_SaysHowManyEntriesAndWhoIsInTheFamilysLog()
    {
        // Arrange
        using var account = new Account(_sessions);
        var model = BringOver(account);

        // Act
        await model.LoadAsync();

        // Assert
        model.Summary.Should().Be("This phone has 42 entries for Maja.");
        model.Family.Should().Be("Your family's log has: Maja.");
        var mapping = model.Mappings.Should().ContainSingle().Subject;
        mapping.Prompt.Should().Be("Maja on this phone is");
        mapping.Options.Select(o => o.Label).Should().Equal("Maja", "Add as a new baby");
        mapping.Selected.Should().Be(0, "the same name is the same baby by default");
    }

    [Fact]
    public async Task ConfirmCommand_AfterMapping_BringsTheEntriesOverAndCloses()
    {
        // Arrange
        using var account = new Account(_sessions);
        await account.LoadAsync(CancellationToken.None);
        var model = BringOver(account);
        await model.LoadAsync();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.BringCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.Babies[MyMaja] == FamilyMaja), Profile.UserId, Arg.Any<CancellationToken>());
        closed.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmCommand_TheTransactionFails_SaysNothingChangedAndStaysOpen()
    {
        // Arrange
        _moves.BringOverAsync(default!, default, default).ThrowsAsyncForAnyArgs(new IOException("disk full"));
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task KeepCommand_KeepsThemSeparate_ClosesAndMovesNothing()
    {
        // Arrange
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        model.KeepCommand.Execute(null);

        // Assert
        closed.Should().Be(1);
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task LoadAsync_DifferentNamesOneBabyEach_DefaultsToTheFamilysBaby()
    {
        // Arrange: the phone's setup used the default name "Baby".
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(MyMaja, "Baby")]));
        using var account = new Account(_sessions);
        var model = BringOver(account);

        // Act
        await model.LoadAsync();

        // Assert
        model.Mappings.Single().Selected.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_NothingToOffer_ClosesWithoutAToast()
    {
        // Arrange: the phone is already in the account's household, so there is nothing to bring over.
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Family);
        using var account = new Account(_sessions);
        var model = BringOver(account);
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.LoadAsync();

        // Assert
        closed.Should().Be(1);
        model.Toast.Should().BeEmpty();
        model.Mappings.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_HouseholdHasSynced_ClosesWithoutAToastAndMovesNothing()
    {
        // Arrange
        _moves.HasEverSyncedAsync(Mine, Arg.Any<CancellationToken>()).Returns(true);
        using var account = new Account(_sessions);
        var model = BringOver(account);
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.LoadAsync();

        // Assert
        closed.Should().Be(1);
        model.Toast.Should().BeEmpty();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task LoadAsync_NoMatchingNameAndSeveralBabies_DefaultsToAddAsNew()
    {
        // Arrange
        _moves.SummaryAsync(Family, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new LocalBaby(FamilyMaja, "Maja"), new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b8")), "Jan")]));
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(MyMaja, "Baby")]));
        using var account = new Account(_sessions);
        var model = BringOver(account);

        // Act
        await model.LoadAsync();

        // Assert
        var mapping = model.Mappings.Single();
        mapping.Options[mapping.Selected].Target.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmCommand_SignedOutBeforeTheMove_SaysNothingChangedAndCloses()
    {
        // Arrange: the session is gone, so nothing moved and nothing is left to ask.
        _sessions.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new StoredSession("a", DateTimeOffset.UnixEpoch, "r", Profile, Family, JoinPending: true),
            (StoredSession?)null);
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(1);
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
    }

    [Fact]
    public async Task ConfirmCommand_NothingChangedButStillPending_SaysSoAndStaysOpen()
    {
        // Arrange: the account's generation moved before the move, yet the stored session is still join-pending.
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]);
        });
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(0);
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task ConfirmCommand_MovedButTheJoinWasNotCompleted_ClosesWithoutAToast()
    {
        // Arrange: the sign-out lands during the move, so the join cannot complete afterwards.
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(async _ =>
        {
            await account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return true;
        });
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(1);
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task NotNowCommand_WhileAgreeIsInFlight_IsIgnored()
    {
        // Arrange
        var release = new TaskCompletionSource();
        _consentApi.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var model = Consent();
        var agreeing = model.AgreeCommand.ExecuteAsync(null);

        // Act
        await model.NotNowCommand.ExecuteAsync(null);
        release.SetResult();
        await agreeing;

        // Assert
        await _state.DidNotReceive().SetConsentAsync(Profile.UserId, ConsentState.Declined, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgreeCommand_TheLocalWriteFails_SaysSoAndStaysOpen()
    {
        // Arrange
        _state.SetConsentAsync(default, default, default).ReturnsForAnyArgs(Task.FromException(new IOException("disk full")));
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.AgreeCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(0);
        model.Toast.Should().Be("Sharing couldn't be turned on. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }
}
