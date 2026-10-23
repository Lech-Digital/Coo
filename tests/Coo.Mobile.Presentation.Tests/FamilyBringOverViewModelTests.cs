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

/// <summary>Spec 011b §6 as the person sees it: the summary, the mapping, the copy, the question about the old family.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilyBringOverViewModelTests : IDisposable
{
    private static readonly HouseholdId Old = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly BabyId MyLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId MyLeo = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly BabyId TheirLena = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));
    private static readonly BabyId TheirOla = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b8"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly Families _families;

    public FamilyBringOverViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        var households = Substitute.For<IHouseholdStore>();
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Old), Settings(Other)]);
        _families = new Families(_account, Substitute.For<IFamilyStore>(), Substitute.For<IAccountApi>(), _api, _sessions, households,
            Substitute.For<IHouseholdMoves>(), Substitute.For<ISyncState>(), Substitute.For<IFamilyRecords>(),
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
    }

    public void Dispose()
    {
        _families.Dispose();
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    private FamilyBringOverViewModel Model() => new(new LeaveFamily(_api, _families, SyncControls.PassThrough(), _account));

    private static BringOverChoice Offer(LocalBaby[] mine, LocalBaby[] theirs, int entries = 42) => new(new LocalSummary(entries, mine), theirs);

    private static BringOverRequest Request(
        BringOverChoice choice, Func<IReadOnlyDictionary<BabyId, BabyId?>, CancellationToken, Task<BringOutcome>> execute,
        Func<CancellationToken, Task>? keep = null) =>
        new(Old, choice, "Their family's log has: Lena.", "in Andrzej's family", execute, keep, "You've joined Andrzej's family.");

    private static Task<BringOutcome> Outcome(BringPath path, OldFamilyQuestion question, int skipped = 0) =>
        Task.FromResult(new BringOutcome(path, 40, skipped, question));

    private static BringOverRequest Copying(OldFamilyQuestion question, int skipped = 0) =>
        Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => Outcome(BringPath.Copied, question, skipped));

    private static FamilyOnPhone Target(Role role, string ownerName) =>
        new(Other, ["Lena"], new FamilyMembership(Other, role, Now, ownerName, 2), false, false, false, true);

    private static BringOverRequest FromSettings(FamilyOnPhone target, BringPath path, OldFamilyQuestion question) =>
        BringOverRequest.ForSettings(Old, Offer([new LocalBaby(MyLena, "Lena")], []), target, (_, _) => Outcome(path, question));

    [Theory]
    [InlineData(Role.Parent, "Andrzej", "Your entries are now in Andrzej's family.")]
    [InlineData(Role.Owner, "Kasia", "Your entries are now in your family.")]
    [InlineData(Role.Parent, "", "Your entries are now in the other family.")]
    public async Task ConfirmCommand_MovedFromSettings_SaysWhereTheEntriesAreWithoutToo(Role role, string ownerName, string expected)
    {
        // Arrange: final review U3, a move leaves nothing behind, so it is not "too"; the target is named as Settings names it.
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(FromSettings(Target(role, ownerName), BringPath.Moved, OldFamilyQuestion.None));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be(expected);
    }

    [Theory]
    [InlineData(Role.Parent, "Andrzej", "Your entries are now in Andrzej's family too.")]
    [InlineData(Role.Owner, "Kasia", "Your entries are now in your family too.")]
    [InlineData(Role.Parent, "", "Your entries are now in the other family too.")]
    public async Task ConfirmCommand_CopiedFromSettings_SaysTheEntriesAreInTheTargetToo(Role role, string ownerName, string expected)
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(FromSettings(Target(role, ownerName), BringPath.Copied, OldFamilyQuestion.None));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be(expected);
    }

    [Fact]
    public async Task ConfirmCommand_CopiedFromSettingsIntoTheirOwnFamily_AsksAboutTheOldFamilyNamingItYours()
    {
        // Arrange
        var model = Model();
        model.Start(FromSettings(Target(Role.Owner, "Kasia"), BringPath.Copied, OldFamilyQuestion.LeaveOld));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Question.Should().StartWith("Your entries are now in your family too. Leave your old family?");
    }

    [Fact]
    public async Task ConfirmCommand_CopiedFromSettingsIntoANamelessFamily_AsksToDeleteTheOldFamilyWithoutAName()
    {
        // Arrange
        var model = Model();
        model.Start(FromSettings(Target(Role.Parent, ""), BringPath.Copied, OldFamilyQuestion.DeleteOld));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Question.Should().Be(
            "Your entries are now in the other family too. Delete your old family? Its 42 entries for Lena are deleted from Coo's server and this phone. The copies stay.");
    }

    [Fact]
    public async Task ConfirmCommand_MovedFromSettingsInPolish_UsesTheLocativeForYourFamily()
    {
        // Arrange
        Strings.Use(new LanguagePreference("pl"), null);
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(FromSettings(Target(Role.Owner, "Kasia"), BringPath.Moved, OldFamilyQuestion.None));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("Twoje wpisy są teraz w Twojej rodzinie.");
    }

    [Fact]
    public async Task KeepOldCommand_WhileTheOldFamilyIsBeingLeft_IsIgnored()
    {
        // Arrange: final review minor, "Keep it" must not close the sheet under a leave that is still running.
        var model = Model();
        var finished = new List<string>();
        model.Finished += (_, t) => finished.Add(t);
        var leaving = new TaskCompletionSource<LeaveOutcome>();
        _api.LeaveAsync(Old, true, Arg.Any<CancellationToken>()).Returns(leaving.Task);
        model.Start(Copying(OldFamilyQuestion.DeleteOld));
        await model.ConfirmCommand.ExecuteAsync(null);
        var answer = model.AnswerCommand.ExecuteAsync(null);

        // Act
        model.KeepOldCommand.Execute(null);

        // Assert
        finished.Should().BeEmpty();
        leaving.SetResult(LeaveOutcome.Deleted);
        await answer;
        finished.Should().Equal("");
    }

    [Fact]
    public void Start_BabiesWithMatchingAndNewNames_DefaultsByNameThenToANewBaby()
    {
        // Arrange
        var model = Model();

        // Act
        model.Start(Request(
            Offer([new LocalBaby(MyLena, " lena "), new LocalBaby(MyLeo, "Leo")], [new LocalBaby(TheirOla, "Ola"), new LocalBaby(TheirLena, "Lena")]),
            (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None)));

        // Assert
        model.Summary.Should().Be("This phone has 42 entries for  lena , Leo.");
        model.Mappings.Select(m => m.Selected).Should().Equal(1, 2);
        model.Mappings[0].Options.Select(o => o.Target).Should().Equal(TheirOla, TheirLena, null);
        model.Step.Should().Be(BringOverStep.Choice);
    }

    [Fact]
    public void Start_OneBabyOnEachSideWithDifferentNames_MapsThemToEachOther()
    {
        // Arrange
        var model = Model();

        // Act
        model.Start(Request(Offer([new LocalBaby(MyLeo, "Leo")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None)));

        // Assert
        model.Mappings.Single().Selected.Should().Be(0);
    }

    [Fact]
    public void Start_NoKeepAction_DoesNotOfferKeeping()
    {
        // Arrange
        var model = Model();

        // Act
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], []), (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None)));

        // Assert
        model.CanKeep.Should().BeFalse();
    }

    [Fact]
    public async Task BringCommand_BabiesToMap_ShowsTheMappingBeforeAnythingMoves()
    {
        // Arrange
        var model = Model();
        var executed = 0;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) =>
        {
            executed++;
            return Outcome(BringPath.Moved, OldFamilyQuestion.None);
        }));

        // Act
        await model.BringCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(BringOverStep.Mapping);
        executed.Should().Be(0);
    }

    [Fact]
    public async Task BringCommand_NoBabiesToMap_BringsStraightAway()
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(Request(Offer([], [], 0), (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None)));

        // Act
        await model.BringCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task ConfirmCommand_ChosenMapping_PassesItToTheMove()
    {
        // Arrange
        var model = Model();
        IReadOnlyDictionary<BabyId, BabyId?>? received = null;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena"), new LocalBaby(MyLeo, "Leo")], [new LocalBaby(TheirLena, "Lena")]), (mapping, _) =>
        {
            received = mapping;
            return Outcome(BringPath.Moved, OldFamilyQuestion.None);
        }));
        model.Mappings[1].Selected = 0;

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        received.Should().BeEquivalentTo(new Dictionary<BabyId, BabyId?> { [MyLena] = TheirLena, [MyLeo] = TheirLena });
    }

    [Fact]
    public async Task ConfirmCommand_NewBabyChosen_MapsToNull()
    {
        // Arrange
        var model = Model();
        IReadOnlyDictionary<BabyId, BabyId?>? received = null;
        model.Start(Request(Offer([new LocalBaby(MyLeo, "Leo")], [new LocalBaby(TheirLena, "Lena"), new LocalBaby(TheirOla, "Ola")]), (mapping, _) =>
        {
            received = mapping;
            return Outcome(BringPath.Moved, OldFamilyQuestion.None);
        }));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        received!.Single().Value.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmCommand_AlreadyCopied_FinishesWithTheDoneToastAndAsksNothing()
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => Outcome(BringPath.AlreadyCopied, OldFamilyQuestion.None)));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("You've joined Andrzej's family.");
        model.Step.Should().Be(BringOverStep.Choice);
    }

    [Fact]
    public async Task ConfirmCommand_CopiedWithNothingToAsk_FinishesWithTheCopiedToast()
    {
        // Arrange: the Owner of a family with other members cannot leave it (§6.2).
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(Copying(OldFamilyQuestion.None));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("Your entries are now in Andrzej's family too.");
    }

    [Fact]
    public async Task ConfirmCommand_CopiedAndTheOwnerIsAlone_AsksToDeleteTheOldFamilyWithoutDeletingIt()
    {
        // Arrange
        var model = Model();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        model.Start(Copying(OldFamilyQuestion.DeleteOld, skipped: 1));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(BringOverStep.OldFamily);
        model.Question.Should().StartWith("Your entries are now in Andrzej's family too. Delete your old family?");
        model.QuestionNote.Should().Be("1 entry can't be brought over and is deleted with it.");
        model.AnswerText.Should().Be("Delete old family");
        finished.Should().Be(0);
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task ConfirmCommand_CopiedEverything_AsksToDeleteWithoutANote()
    {
        // Arrange
        var model = Model();
        model.Start(Copying(OldFamilyQuestion.DeleteOld));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.QuestionNote.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmCommand_CopiedAndAParent_AsksToLeaveTheOldFamily()
    {
        // Arrange
        var model = Model();
        model.Start(Copying(OldFamilyQuestion.LeaveOld));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(BringOverStep.OldFamily);
        model.Question.Should().Be("Your entries are now in Andrzej's family too. Leave your old family? Its log is removed from this phone. Entries you added stay with that family.");
        model.QuestionNote.Should().BeEmpty();
        model.AnswerText.Should().Be("Leave");
    }

    [Fact]
    public async Task AnswerCommand_DeleteOldFamilyConfirmed_LeavesTheOldFamilyWithConfirmAndFinishesQuietly()
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(Copying(OldFamilyQuestion.DeleteOld));
        await model.ConfirmCommand.ExecuteAsync(null);

        // Act
        await model.AnswerCommand.ExecuteAsync(null);

        // Assert
        await _api.Received(1).LeaveAsync(Old, true, Arg.Any<CancellationToken>());
        toast.Should().Be("");
    }

    [Fact]
    public async Task AnswerCommand_ServerRefuses_ShowsItsMessageAndKeepsTheQuestion()
    {
        // Arrange
        var model = Model();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        _api.LeaveAsync(Old, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException("household.confirmation-required"));
        model.Start(Copying(OldFamilyQuestion.LeaveOld));
        await model.ConfirmCommand.ExecuteAsync(null);

        // Act
        await model.AnswerCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("This family still has entries on Coo's server. Confirm to delete them.");
        model.Step.Should().Be(BringOverStep.OldFamily);
        finished.Should().Be(0);
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task AnswerCommand_NoAnswerFromTheServer_SaysItDidNotWorkAndKeepsTheQuestion()
    {
        // Arrange
        var model = Model();
        _api.LeaveAsync(Old, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        model.Start(Copying(OldFamilyQuestion.DeleteOld));
        await model.ConfirmCommand.ExecuteAsync(null);

        // Act
        await model.AnswerCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        model.Step.Should().Be(BringOverStep.OldFamily);
    }

    [Fact]
    public async Task AnswerCommand_ThrowsSomethingUnexpected_ShowsTheToastAndNeverEscapes()
    {
        // Arrange
        var model = Model();
        _api.LeaveAsync(Old, true, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        model.Start(Copying(OldFamilyQuestion.DeleteOld));
        await model.ConfirmCommand.ExecuteAsync(null);

        // Act
        await model.AnswerCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task KeepOldCommand_OnTheQuestion_FinishesQuietlyAndLeavesNothing()
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        model.Start(Copying(OldFamilyQuestion.DeleteOld));
        await model.ConfirmCommand.ExecuteAsync(null);

        // Act
        model.KeepOldCommand.Execute(null);

        // Assert
        toast.Should().Be("");
        await _api.DidNotReceiveWithAnyArgs().LeaveAsync(default, default, default);
    }

    [Fact]
    public async Task ConfirmCommand_MoveFails_SaysNothingWasChangedAndDoesNotFinish()
    {
        // Arrange
        var model = Model();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => throw new InvalidOperationException("rolled back")));

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
        finished.Should().Be(0);
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmCommand_PressedTwiceWhileBusy_MovesOnce()
    {
        // Arrange
        var model = Model();
        var gate = new TaskCompletionSource<BringOutcome>();
        var executed = 0;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) =>
        {
            executed++;
            return gate.Task;
        }));

        // Act
        var first = model.ConfirmCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);
        gate.SetResult(new BringOutcome(BringPath.Moved, 1, 0, OldFamilyQuestion.None));
        await first;

        // Assert
        executed.Should().Be(1);
    }

    [Fact]
    public async Task KeepCommand_InTheJoinFlow_KeepsBothAndFinishesWithTheBothFamiliesToast()
    {
        // Arrange
        var model = Model();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        var kept = 0;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None),
            _ =>
            {
                kept++;
                return Task.CompletedTask;
            }));

        // Act
        await model.KeepCommand.ExecuteAsync(null);

        // Assert
        kept.Should().Be(1);
        toast.Should().Be("Both families are on this phone. Switch between them in Settings → Family.");
    }

    [Fact]
    public async Task KeepCommand_KeepingFails_ShowsTheToastAndDoesNotFinish()
    {
        // Arrange
        var model = Model();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        model.Start(Request(Offer([new LocalBaby(MyLena, "Lena")], [new LocalBaby(TheirLena, "Lena")]), (_, _) => Outcome(BringPath.Moved, OldFamilyQuestion.None),
            _ => throw new IOException("disk")));

        // Act
        await model.KeepCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
        finished.Should().Be(0);
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task KeepCommand_NoKeepAction_DoesNothing()
    {
        // Arrange
        var model = Model();
        var finished = 0;
        model.Finished += (_, _) => finished++;
        model.Start(Copying(OldFamilyQuestion.None));

        // Act
        await model.KeepCommand.ExecuteAsync(null);

        // Assert
        finished.Should().Be(0);
    }
}
