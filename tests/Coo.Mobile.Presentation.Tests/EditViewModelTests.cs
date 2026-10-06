using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Edit;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The edit form (Spec 007 §3, §4): inline errors block Save, warnings never do.</summary>
public sealed class EditViewModelTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public EditViewModelTests()
    {
        // East of UTC, as on the phone: an unset date (0001-01-01) is no valid time here.
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Maja", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
    }

    [Fact]
    public async Task LoadAsync_EastOfUtc_FillsTheFormWithoutThrowing()
    {
        // Arrange: found on the phone; warnings were computed before the dates were set, and threw.
        var entry = Diaper();
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        var model = Model();

        // Act
        Func<Task> act = () => model.LoadAsync(entry.Id);

        // Assert
        await act.Should().NotThrowAsync();
        model.Contents.Should().Be(DiaperContents.Dirty);
        model.StartTime.Should().Be(new TimeSpan(13, 55, 0), "12:00 UTC less 5 minutes, at +02:00");
    }

    [Fact]
    public async Task Save_BottleOver400Ml_WarnsAndStillSaves()
    {
        // Arrange
        var model = Model();
        await model.NewAsync("feeding");
        model.IsBottleForm = true;

        // Act
        model.Amount = "500";
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Warnings.Should().NotBeEmpty();
        await _store.Received(1).SaveAsync(Arg.Is<CareEntry>(e => e.Details is BottleFeed && e.Source == EntrySource.Manual), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_BreastFeedWithoutMinutes_ShowsAnErrorAndSavesNothing()
    {
        // Arrange: there is no untimed breast feed (Spec 006 Q2).
        var model = Model();
        await model.NewAsync("feeding");

        // Act
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().NotBeEmpty();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    private EditViewModel Model()
    {
        var undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), new UndoFeedMerge(_store, _clock, _time), _store, _time);
        return new EditViewModel(_store, _households, new LogEntry(_store, _clock, _time), new ReviseEntry(_store, _clock, _time), undo, _time);
    }

    private CareEntry Diaper() =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(_time.GetUtcNow())), Household, EntrySubject.Baby(Baby),
            new DiaperDetails(DiaperContents.Dirty), EntryTiming.Point(_time.GetUtcNow().AddMinutes(-5)), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), _time.GetUtcNow()).Value;
}
