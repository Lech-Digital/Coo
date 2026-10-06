using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §2: the sync line's states. It never says "up to date" while the outbox holds anything.</summary>
[Collection(nameof(StringsState))]
public sealed class SyncLineTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly AccountState Signed = new SignedIn(Profile);
    private static readonly DateTimeOffset At = new(2026, 10, 2, 14, 32, 0, TimeSpan.FromHours(2));

    public SyncLineTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static SyncStatus Status(CycleOutcome? last, int pending = 0, bool running = false, DateTimeOffset? at = null) =>
        SyncStatus.Initial with { Last = last, Pending = pending, IsRunning = running, LastSyncedAt = at };

    [Fact]
    public void For_SyncedWithNothingPending_SaysUpToDateWithTheTime()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView($"Up to date · {new Formats(true, VolumeUnit.Millilitres).Time(At)}", SyncLineAction.None));
    }

    [Theory]
    [InlineData(1, "Sharing 1 change…")]
    [InlineData(3, "Sharing 3 changes…")]
    public void For_ChangesPending_NeverSaysUpToDate(int pending, string expected)
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, pending, at: At), is24Hour: true);

        // Assert
        line.Text.Should().Be(expected);
    }

    [Fact]
    public void For_FailedWithTwoPending_SaysTheyAreSafe()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2), is24Hour: true);

        // Assert
        line.Text.Should().Be("Waiting for a connection. 2 changes are safe on this phone.");
    }

    [Fact]
    public void For_FailedWithNothingPending_SaysOnlyThatItWaits()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed), is24Hour: true);

        // Assert
        line.Text.Should().Be("Waiting for a connection.");
    }

    [Fact]
    public void For_RetryingAfterAFailureWithChangesPending_SaysSharing()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2, running: true), is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing 2 changes…");
    }

    [Theory]
    [InlineData(CycleOutcome.ConsentDeclined)]
    [InlineData(CycleOutcome.ConsentNeeded)]
    public void For_NoConsent_SaysSharingIsOffAndOffersToTurnItOn(CycleOutcome outcome)
    {
        // Act
        var line = SyncLine.For(Signed, Status(outcome, 4), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Sharing is off. Your log is only on this phone.", SyncLineAction.TurnOnSharing));
    }

    [Fact]
    public void For_ClientTooOld_AsksForAnUpdate()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.ClientTooOld, 4), is24Hour: true);

        // Assert
        line.Text.Should().Be("Update Coo to keep sharing. Your changes are safe on this phone.");
    }

    private static readonly AccountState Joining = new JoinPending(Profile, new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9")));

    [Fact]
    public void For_JoinPendingAndTheFamilyPulled_SaysTheEntriesAreNotInTheFamilysLogAndOffersTheChoice()
    {
        // Act
        var line = SyncLine.For(Joining, SyncStatus.Initial with { Last = CycleOutcome.JoinPending, JoinReady = true }, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Signed in. The entries on this phone aren't in your family's log yet.", SyncLineAction.ChooseWhatToDo));
    }

    [Fact]
    public void For_JoinPendingBeforeTheFamilyIsPulled_OffersNoChoiceYet()
    {
        // Act (final review C1 b)
        var line = SyncLine.For(Joining, SyncStatus.Initial, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Signed in. The entries on this phone aren't in your family's log yet.", SyncLineAction.None));
    }

    [Theory]
    [InlineData(CycleOutcome.ConsentNeeded)]
    [InlineData(CycleOutcome.ConsentDeclined)]
    public void For_JoinPendingWithoutConsent_OffersToTurnOnSharing(CycleOutcome last)
    {
        // Act (final review C1 b, Minor 6)
        var line = SyncLine.For(Joining, Status(last), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Sharing is off. Your log is only on this phone.", SyncLineAction.TurnOnSharing));
    }

    [Fact]
    public void For_StoppedInPolish_IsNotThePausedWording()
    {
        // Arrange: "wstrzymane" is Sync.Paused's word; a stop must read differently.
        Strings.Use(new LanguagePreference("pl"), null);

        // Act
        var noProgress = SyncLine.For(Signed, Status(CycleOutcome.Failed) with { LastError = SyncCycle.NoProgressError }, is24Hour: true);

        // Assert
        noProgress.Text.Should().StartWith("Udostępnianie zatrzymane.");
    }

    [Fact]
    public void For_SessionRefused_SaysSignInAgain()
    {
        // Act
        var line = SyncLine.For(new SignedOut(SessionExpired: true), Status(CycleOutcome.NotSignedIn, 3), is24Hour: true);

        // Assert
        line.Text.Should().Be("Sign in again to keep sharing.");
    }

    [Fact]
    public void For_NeverSyncedAndNothingPending_IsEmpty()
    {
        // Act
        var line = SyncLine.For(Signed, SyncStatus.Initial, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("", SyncLineAction.None));
    }

    [Theory]
    [InlineData(SyncCycle.NoProgressError, "Sharing stopped. Your changes are safe on this phone. Coo will try again when you reopen the app.")]
    public void For_StoppedForGood_SaysSoNotThatItWaitsForAConnection(string error, string expected)
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2) with { LastError = error }, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView(expected, SyncLineAction.None));
    }

    [Fact]
    public void For_FailedWithAnotherError_StillSaysItWaits()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2) with { LastError = "HttpRequestException" }, is24Hour: true);

        // Assert
        line.Text.Should().Be("Waiting for a connection. 2 changes are safe on this phone.");
    }

    [Fact]
    public void For_StoppedAndThenSyncedAgain_ForgetsTheStop()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At) with { LastError = null }, is24Hour: true);

        // Assert
        line.Text.Should().StartWith("Up to date");
    }

    [Theory]
    [InlineData(CycleOutcome.SoftLocked)]
    [InlineData(CycleOutcome.Removed)]
    public void For_SoftLockedOrRemoved_SaysSharingIsPausedNotWaitingForAConnection(CycleOutcome outcome)
    {
        // Act
        var line = SyncLine.For(Signed, Status(outcome, 2), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Sharing is paused. Your entries are safe on this phone.", SyncLineAction.None));
    }

    [Fact]
    public void For_SyncedIn12HourFormat_ShowsTheTwelveHourTime()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At), is24Hour: false);

        // Assert
        line.Text.Should().Be($"Up to date · {new Formats(false, VolumeUnit.Millilitres).Time(At)}");
        line.Text.Should().NotBe($"Up to date · {new Formats(true, VolumeUnit.Millilitres).Time(At)}");
    }

    [Fact]
    public void For_ShownFamilyNotAMember_SaysSharingStoppedForThatFamily()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At) with { ShownStopped = true }, is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing stopped: this family is no longer on Coo's server. Your entries are safe on this phone.");
    }

    [Fact]
    public void For_ShownFamilyPausedAndAlsoStopped_ReadsAsPausedNotRemoved()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.SoftLocked) with { ShownStopped = true }, is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing is paused. Your entries are safe on this phone.");
    }

    [Fact]
    public void For_ShownFamilyPausedAlone_ReadsAsPaused()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.SoftLocked), is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing is paused. Your entries are safe on this phone.");
    }
}
