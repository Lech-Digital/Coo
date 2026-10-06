using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Setup;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §8.2: the removal sheet, Today's paused banner, and the setup step's "Enter a code".</summary>
[Collection(nameof(StringsState))]
public sealed class RemovalTests : IDisposable
{
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly BabyId Maja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 1, 12, 0, TimeSpan.Zero);

    private readonly ISyncStatus _status = Substitute.For<ISyncStatus>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly List<Families> _made = [];

    public RemovalTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _clock.Is24Hour.Returns(true);
        _status.Status.Returns(SyncStatus.Initial with { Removal = Joined });
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined)]);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(10, [new LocalBaby(Maja, "Maja")]));
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([]);
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

    private Families Families()
    {
        var families = new Families(_account, Substitute.For<IFamilyStore>(), Substitute.For<IAccountApi>(), Substitute.For<IHouseholdApi>(),
            _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        _made.Add(families);
        return families;
    }

    private RemovalViewModel Removal() => new(_status, _sync, Families(), _launcher, _clock, new FakeTimeProvider(Now));

    private static LiveEntry Bottle(int id, DateTimeOffset at, string? note)
    {
        var entry = CareEntry.Create(new EntryId(Guid.Parse($"7a1c0000-0000-7000-8000-0000000000f{id}")), Joined, EntrySubject.Baby(Maja),
            BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(120).Value).Value, EntryTiming.Point(at), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(1_790_000_000_000, 1, Phone), at).Value;
        if (note is not null)
            entry.SetNote(Note.From(note).Value, new HlcStamp(1_790_000_000_000, 2, Phone));
        return new LiveEntry(entry);
    }

    private static string Line(DateTimeOffset at, string note)
    {
        var local = at.ToLocalTime();
        return $"{local.ToString("ddd d MMM", Strings.Culture)}, {local:HH:mm} · Bottle · Formula · 120 ml" + (note.Length == 0 ? "" : " · " + note);
    }

    [Fact]
    public async Task LoadAsync_NothingUnshared_NamesTheBabyAndShowsNoMiddleParagraph()
    {
        // Act
        var model = Removal();
        await model.LoadAsync();

        // Assert
        RemovalViewModel.Title.Should().Be("You're no longer in this family");
        model.Body.Should().Be("You're no longer a member of the family that logs Maja, so its log will be removed from this phone. Entries you already shared stay with the family.");
        model.Unshared.Should().BeEmpty();
        model.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_FamilyWithNoBaby_UsesTheCopyWithoutAName()
    {
        // Arrange
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        var model = Removal();

        // Act
        await model.LoadAsync();

        // Assert
        model.Body.Should().Be("You're no longer a member of this family, so its log will be removed from this phone. Entries you already shared stay with the family.");
    }

    [Fact]
    public async Task LoadAsync_ThreeUnsharedEntries_SaysHowManyAndListsThemOldestFirst()
    {
        // Arrange
        var first = Now.AddHours(-3);
        var second = Now.AddHours(-2);
        var third = Now.AddHours(-1);
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([Bottle(3, third, null), Bottle(1, first, "night feed"), Bottle(2, second, null)]);
        var model = Removal();

        // Act
        await model.LoadAsync();

        // Assert
        model.Unshared.Should().Be("3 entries you logged weren't shared yet.");
        model.Lines.Should().Equal(Line(first, "night feed"), Line(second, ""), Line(third, ""));
        RemovalViewModel.ShareText.Should().Be("Share them as text");
    }

    [Fact]
    public async Task ShareCommand_OneUnsharedBottle_SharesOneLineInThePersonsLanguageAndClock()
    {
        // Arrange
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([Bottle(1, Now, "night feed")]);
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;
        await model.LoadAsync();

        // Act
        await model.ShareCommand.ExecuteAsync(null);

        // Assert
        model.Unshared.Should().Be("1 entry you logged wasn't shared yet.");
        await _launcher.Received(1).ShareTextAsync(Line(Now, "night feed"), "You're no longer in this family");
        closed.Should().Be(0, "sharing leaves the sheet open");
    }

    [Fact]
    public async Task ShareCommand_NothingUnshared_ShareNothing()
    {
        // Arrange
        var model = Removal();
        await model.LoadAsync();

        // Act
        await model.ShareCommand.ExecuteAsync(null);

        // Assert
        await _launcher.DidNotReceiveWithAnyArgs().ShareTextAsync(default!, default!);
    }

    [Fact]
    public async Task ShareCommand_TheShareSheetFails_SaysSoAndStaysOpen()
    {
        // Arrange
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([Bottle(1, Now, null)]);
        _launcher.ShareTextAsync(default!, default!).ThrowsAsyncForAnyArgs(new InvalidOperationException("no share sheet"));
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;
        await model.LoadAsync();

        // Act
        await model.ShareCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        closed.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_NoRemovalPending_ClosesAtOnce()
    {
        // Arrange
        _status.Status.Returns(SyncStatus.Initial);
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.LoadAsync();

        // Assert
        closed.Should().Be(1);
        model.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_TheFamilyCannotBeRead_StillOffersOkWithTheNeutralCopy()
    {
        // Arrange
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("store"));
        var model = Removal();

        // Act
        await model.LoadAsync();
        await model.OkCommand.ExecuteAsync(null);

        // Assert
        model.Body.Should().StartWith("You're no longer a member of this family");
        await _sync.Received(1).AcknowledgeRemovalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OkCommand_Tapped_AcknowledgesTheRemovalAndCloses()
    {
        // Arrange
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;
        await model.LoadAsync();

        // Act
        await model.OkCommand.ExecuteAsync(null);

        // Assert
        await _sync.Received(1).AcknowledgeRemovalAsync(Arg.Any<CancellationToken>());
        closed.Should().Be(1);
        model.IsBusy.Should().BeFalse();
        RemovalViewModel.OkText.Should().Be("OK");
    }

    [Fact]
    public async Task OkCommand_TappedTwiceWhileBusy_AcknowledgesOnce()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sync.AcknowledgeRemovalAsync(Arg.Any<CancellationToken>()).Returns(gate.Task);
        var model = Removal();
        await model.LoadAsync();

        // Act
        var first = model.OkCommand.ExecuteAsync(null);
        var busy = model.IsBusy;
        await model.OkCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        gate.SetResult();
        await first;

        // Assert
        busy.Should().BeTrue();
        await _sync.Received(1).AcknowledgeRemovalAsync(Arg.Any<CancellationToken>());
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task OkCommand_AcknowledgingFails_SaysSoStaysOpenAndCanBeTappedAgain()
    {
        // Arrange
        _sync.AcknowledgeRemovalAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("store")), Task.CompletedTask);
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;
        await model.LoadAsync();

        // Act
        await model.OkCommand.ExecuteAsync(null);
        var afterFailure = closed;
        await model.OkCommand.ExecuteAsync(null);

        // Assert
        afterFailure.Should().Be(0);
        model.Toast.Should().Be("That didn't work. Check your connection and try again.");
        closed.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ShownFamilyIsTheOnlyOneAndSoftLocked_SaysSharingIsPaused()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var section = new PausedSection(Families(), _status);

        // Act
        await section.RefreshAsync();

        // Assert
        section.Text.Should().Be("Sharing with this family is paused. Your entries are safe on this phone.");
        section.Order.Should().Be(5);
    }

    [Fact]
    public async Task RefreshAsync_SoftLockedButAnotherFamilyExists_ShowsNoBanner()
    {
        // Arrange
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), Settings(Other)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var section = new PausedSection(Families(), _status);

        // Act
        await section.RefreshAsync();

        // Assert
        section.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_TheLockLifts_TheBannerGoes()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true, false);
        var section = new PausedSection(Families(), _status);
        await section.RefreshAsync();

        // Act
        await section.RefreshAsync();

        // Assert
        section.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_TheStoreFails_KeepsWhatItShowed()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var section = new PausedSection(Families(), _status);
        await section.RefreshAsync();
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("store"));

        // Act
        await section.RefreshAsync();

        // Assert
        section.Text.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StatusChanged_AfterACycleSoftLocksTheOnlyFamily_ShowsTheBanner()
    {
        // Arrange
        var section = new PausedSection(Families(), _status);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        section.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PausedSection.Text))
                shown.TrySetResult();
        };
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        _status.StatusChanged += Raise.Event();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        section.Text.Should().Be("Sharing with this family is paused. Your entries are safe on this phone.");
    }

    [Fact]
    public async Task RefreshForAsync_TheSameFamilyAgain_DoesNotAskTheStoreAgain()
    {
        // Arrange
        var section = new PausedSection(Families(), _status);
        await section.RefreshForAsync(Joined);
        _households.ClearReceivedCalls();

        // Act
        await section.RefreshForAsync(Joined);
        var again = _households.ReceivedCalls().Count();
        await section.RefreshForAsync(Other);

        // Assert
        again.Should().Be(0, "Today ticks every second");
        _households.ReceivedCalls().Should().NotBeEmpty("another family shown is checked");
    }

    [Fact]
    public async Task RefreshForAsync_TheFirstCheckFails_TheNextUpdateChecksAgain()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<HouseholdSettings>>(new InvalidOperationException("store")), Task.FromResult<IReadOnlyList<HouseholdSettings>>([Settings(Joined)]));
        var section = new PausedSection(Families(), _status);
        await section.RefreshForAsync(Joined);

        // Act
        await section.RefreshForAsync(Joined);

        // Assert
        section.Text.Should().Be("Sharing with this family is paused. Your entries are safe on this phone.");
    }

    [Fact]
    public async Task Update_AFamilyIsShown_ChecksAndSetsTheText()
    {
        // Arrange
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var section = new PausedSection(Families(), _status);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        section.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PausedSection.Text))
                shown.TrySetResult();
        };
        var baby = BabyProfile.Create(Maja, Joined, "Maja", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Phone), new DateOnly(2026, 10, 1)).Value;

        // Act
        section.Update(new HomeSnapshot(Settings(Joined), baby, null, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres)));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        section.Text.Should().Be("Sharing with this family is paused. Your entries are safe on this phone.");
    }

    [Fact]
    public async Task OkCommand_AHandlerOfClosedThrows_DoesNotToastAFailureAfterTheAcknowledge()
    {
        // Arrange
        var model = Removal();
        model.Closed += (_, _) => throw new InvalidOperationException("page gone");
        await model.LoadAsync();

        // Act
        await model.OkCommand.ExecuteAsync(null);

        // Assert
        await _sync.Received(1).AcknowledgeRemovalAsync(Arg.Any<CancellationToken>());
        model.Toast.Should().BeEmpty();
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task ShareCommand_TappedTwiceWhileSharing_SharesOnce()
    {
        // Arrange
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([Bottle(1, Now, null)]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _launcher.ShareTextAsync(default!, default!).ReturnsForAnyArgs(gate.Task);
        var model = Removal();
        await model.LoadAsync();

        // Act
        var first = model.ShareCommand.ExecuteAsync(null);
        await model.ShareCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        gate.SetResult();
        await first;

        // Assert
        await _launcher.ReceivedWithAnyArgs(1).ShareTextAsync(default!, default!);
    }

    [Fact]
    public void EnterCodeCommand_Tapped_AsksTheHostToOpenTheCodeSheet()
    {
        // Arrange
        var model = new SetupViewModel(_households, new SetUpBaby(_households, Substitute.For<IVersionClock>(), new FakeTimeProvider(Now)), new FakeTimeProvider(Now));
        var requested = 0;
        model.EnterCodeRequested += (_, _) => requested++;

        // Act
        model.EnterCodeCommand.Execute(null);

        // Assert
        requested.Should().Be(1);
        SetupViewModel.HaveInvitationText.Should().Be("Have an invitation? Enter a code");
    }
}
