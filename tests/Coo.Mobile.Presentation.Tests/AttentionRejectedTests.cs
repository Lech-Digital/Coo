using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §2: rejected changes fill "Not accepted by the server", with the reason, Edit and Delete; usable at hundreds.</summary>
[Collection(nameof(StringsState))]
public sealed class AttentionRejectedTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.FromHours(2));

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IDuplicateDismissalStore _dismissals = Substitute.For<IDuplicateDismissalStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();

    public AttentionRejectedTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Phone)).Value]);
        _store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _dismissals.ListAsync(Household, Arg.Any<CancellationToken>()).Returns(new HashSet<EntryPair>());
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private CareEntry Diaper(int n)
    {
        var id = new EntryId(Guid.Parse("7a1c0000-0000-7000-9000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)));
        return CareEntry.Create(id, Household, EntrySubject.Baby(Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddMinutes(-n)),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(n + 1, 0, Phone), Now).Value;
    }

    private void Rejected(int count)
    {
        var entries = Enumerable.Range(1, count).Select(Diaper).ToList();
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [.. entries.Select(e => new SyncRejection(SyncEntityTypes.CareEntry, e.Id.Value, "time.end-not-after-start", Now))]);
        foreach (var entry in entries)
            _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
    }

    private AttentionViewModel Model()
    {
        var clockFormat = Substitute.For<IClockFormat>();
        clockFormat.Is24Hour.Returns(true);
        var undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), _store, _time);
        return new AttentionViewModel(_households, new GetAttention(_store, _dismissals, _sync), new DismissDuplicate(_dismissals, _clock, _time), undo, clockFormat, _time);
    }

    [Fact]
    public async Task RefreshAsync_ARejectedEntry_IsListedWithItsReasonAndCounted()
    {
        // Arrange
        Rejected(1);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var item = model.Rejected.Should().ContainSingle().Subject;
        item.Reason.Should().Be("The end must be after the start.");
        model.IsEmpty.Should().BeFalse();
        (await new GetAttention(_store, _dismissals, _sync).ExecuteAsync(Household, Now, CancellationToken.None)).Count.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ThreeHundredRejected_ShowsFiftyAndMoreOnRequest()
    {
        // Arrange: a parent who ignored updates for a month (ADR 0002 §11).
        Rejected(300);
        var model = Model();
        await model.RefreshAsync();
        var first = model.Rejected.Count;

        // Act
        model.ShowMoreRejectedCommand.Execute(null);

        // Assert
        first.Should().Be(AttentionViewModel.RejectedPageSize);
        model.Rejected.Count.Should().Be(2 * AttentionViewModel.RejectedPageSize);
        model.RejectedTotal.Should().Be(300);
        model.HasMoreRejected.Should().BeTrue();
    }

    [Fact]
    public async Task EditCommand_ARejectedEntry_AsksThePageToOpenIt()
    {
        // Arrange
        Rejected(1);
        var model = Model();
        await model.RefreshAsync();
        EntryId? opened = null;
        model.EditRequested += (_, id) => opened = id;

        // Act
        model.EditRejectedCommand.Execute(model.Rejected[0].Id);

        // Assert
        opened.Should().Be(model.Rejected[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_RejectedEntryDeletedSince_IsNotListed()
    {
        // Arrange
        Rejected(1);
        var entry = Diaper(1);
        entry.Delete(new HlcStamp(100, 0, Phone));
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void ExternalChange_FromSync_RaisesDataChanged()
    {
        // Arrange
        using var undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), _store, _time);
        var raised = 0;
        undo.DataChanged += (_, _) => raised++;

        // Act
        undo.ExternalChange();

        // Assert
        raised.Should().Be(1);
    }
}
