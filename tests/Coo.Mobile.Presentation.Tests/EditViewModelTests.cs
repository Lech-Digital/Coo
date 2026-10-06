using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Edit;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The edit form (Spec 007 §3, §4): inline errors block Save, warnings never do.</summary>
[Collection(nameof(StringsState))]
public sealed class EditViewModelTests : IDisposable
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
        Strings.Use(new LanguagePreference("en"), null);
        // East of UTC, as on the phone: an unset date (0001-01-01) is no valid time here.
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Maja", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
    }

    private const string ChangedElsewhere = "This entry changed on another phone. Have a look and save again.";

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

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task LoadAsync_AFeedInParts_ListsEachPartReadOnly()
    {
        // Arrange: left and right back to back, a pause, then left again (Spec 011d §5).
        var feed = InParts((BreastSide.Left, -60, -50), (BreastSide.Right, -50, -45), (BreastSide.Left, -40, -36));
        var model = Model();

        // Act
        await model.LoadAsync(feed.Id);

        // Assert
        model.IsInParts.Should().BeTrue();
        model.PartLines.Should().Equal("Part 1 · 13:00–13:15 · left, right", "Part 2 · 13:20–13:24 · left");
    }

    [Fact]
    public async Task LoadAsync_ASideSwitchWithoutAPause_IsNotInParts()
    {
        // Arrange
        var feed = InParts((BreastSide.Left, -60, -50), (BreastSide.Right, -50, -45));
        var model = Model();

        // Act
        await model.LoadAsync(feed.Id);

        // Assert
        model.IsInParts.Should().BeFalse();
        model.PartLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_AFeedInPartsMovedTenMinutesLater_ShiftsEverySegment()
    {
        // Arrange
        var feed = InParts((BreastSide.Left, -60, -50), (BreastSide.Right, -47, -42), (BreastSide.Left, -40, -36));
        var before = ((BreastFeed)feed.Details).Segments;
        var saved = new List<CareEntry>();
        await _store.SaveAsync(Arg.Do<CareEntry>(saved.Add), Arg.Any<CancellationToken>());
        var model = Model();
        await model.LoadAsync(feed.Id);

        // Act
        model.StartTime += TimeSpan.FromMinutes(10);
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().BeEmpty();
        var segments = ((BreastFeed)saved.Should().ContainSingle().Which.Details).Segments;
        segments.Should().Equal(before.Select(s => s with { Start = s.Start.AddMinutes(10), End = s.End!.Value.AddMinutes(10) }));
    }

    [Fact]
    public async Task Save_AFeedInPartsWithANewNote_KeepsEverySegment()
    {
        // Arrange: a start with seconds, as the timer writes it; the form shows whole minutes.
        var feed = InParts((BreastSide.Left, -60, -50), (BreastSide.Right, -47, -42), (BreastSide.Left, -40, -36), seconds: 17);
        var saved = new List<CareEntry>();
        await _store.SaveAsync(Arg.Do<CareEntry>(saved.Add), Arg.Any<CancellationToken>());
        var model = Model();
        await model.LoadAsync(feed.Id);

        // Act
        model.Note = "sleepy";
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        var written = saved.Should().ContainSingle("only the note changed").Which;
        written.Note!.Text.Should().Be("sleepy");
        ((BreastFeed)written.Details).Parts.Should().Be(3);
        written.Details.Should().Be(feed.Details);
    }

    [Fact]
    public async Task Save_AOnePartFeedMergedOnTheOtherPhoneWhileOpen_WritesNothingAndReloadsTheParts()
    {
        // Arrange: the form opened a one-part timer feed; the other phone merged it, and the pull brought the merge.
        var single = InParts([(BreastSide.Left, -60, -50)], 17);
        var model = Model();
        await model.LoadAsync(single.Id);
        Merged(single, (BreastSide.Left, -60, -50), (BreastSide.Right, -47, -42), (BreastSide.Left, -40, -36));
        var closed = false;
        model.Closed += (_, _) => closed = true;

        // Act
        model.Note = "sleepy";
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        model.Error.Should().Be(ChangedElsewhere);
        closed.Should().BeFalse("the form stays open to show what is stored now");
        model.IsInParts.Should().BeTrue("the form now shows the merged feed");
        model.PartLines.Should().HaveCount(3);
        model.Note.Should().BeEmpty("the form shows the stored note");
    }

    [Fact]
    public async Task Save_AOnePartFeedMergedMeanwhileMovedTenMinutes_WritesNothingAndReloadsTheStart()
    {
        // Arrange
        var single = InParts([(BreastSide.Left, -60, -50)], 17);
        var model = Model();
        await model.LoadAsync(single.Id);
        var loadedStart = model.StartTime;
        Merged(single, (BreastSide.Left, -60, -50), (BreastSide.Right, -47, -42));

        // Act
        model.StartTime += TimeSpan.FromMinutes(10);
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        model.Error.Should().Be(ChangedElsewhere);
        model.StartTime.Should().Be(loadedStart);
    }

    [Fact]
    public async Task Save_ASinglePartFeedMergedMeanwhileWithShortPauses_WritesNothingAndReloads()
    {
        // Arrange: a timer feed, left for 3 minutes. On the other phone it was merged with a right feed that began 30
        // seconds after it stopped: the pause is too short for a part, so the merged feed is one part with two segments.
        var feed = InParts([(BreastSide.Left, -60, -57)], 17);
        var model = Model();
        await model.LoadAsync(feed.Id);
        var start = ((BreastFeed)feed.Details).FirstStart;
        var merged = BreastFeed.Create(
            [new BreastSegment(BreastSide.Left, start, start.AddMinutes(3)), new BreastSegment(BreastSide.Right, start.AddSeconds(210), start.AddSeconds(390))],
            isCompleted: true).Value;
        feed.Revise<BreastFeed>(_ => Result<BreastFeed>.Ok(merged), new HlcStamp(++_wall, 0, Device), _time.GetUtcNow());
        merged.Parts.Should().Be(1);
        model.RightMinutes.Should().BeEmpty();

        // Act: Save with nothing changed, which would have flattened the merge back to the left side only.
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        model.Error.Should().Be(ChangedElsewhere);
        model.RightMinutes.Should().Be("3", "the form now shows the merged feed");
    }

    [Fact]
    public async Task Save_ANoteOnAnEntryChangedOnTheOtherPhone_WritesNothing()
    {
        // Arrange
        var diaper = Diaper();
        _store.FindAsync(diaper.Id, Arg.Any<CancellationToken>()).Returns(_ => new LiveEntry(diaper));
        var model = Model();
        await model.LoadAsync(diaper.Id);
        diaper.SetNote(Note.From("from the other phone").Value, new HlcStamp(++_wall, 0, Device));

        // Act
        model.Note = "mine";
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        model.Error.Should().Be(ChangedElsewhere);
        model.Note.Should().Be("from the other phone");
    }

    [Fact]
    public async Task Undo_AfterASave_PutsBackTheEntryAsStoredBeforeTheSave()
    {
        // Arrange
        var stored = Stored(Diaper());
        var model = Model(out var undo);
        await model.LoadAsync(stored().Id);
        var before = stored().Version;

        // Act
        model.Contents = DiaperContents.Wet;
        await model.SaveCommand.ExecuteAsync(null);
        stored().Details.Should().Be(new DiaperDetails(DiaperContents.Wet));
        await undo.UndoAsync();

        // Assert
        stored().Details.Should().Be(new DiaperDetails(DiaperContents.Dirty));
        stored().Version.Should().BeGreaterThan(before, "undo writes the old values as a new revision");
    }

    [Fact]
    public async Task LoadAsync_AFeedInParts_RaisesIsInPartsAndPartLines()
    {
        // Arrange
        var feed = InParts((BreastSide.Left, -60, -50), (BreastSide.Right, -47, -42), (BreastSide.Left, -40, -36));
        var model = Model();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        await model.LoadAsync(feed.Id);

        // Assert
        changed.Should().Contain([nameof(EditViewModel.IsInParts), nameof(EditViewModel.PartLines)]);
    }

    /// <summary>The store now holds <paramref name="entry"/> as merged on the other phone: its segments, with seconds.</summary>
    private BreastFeed Merged(CareEntry entry, params (BreastSide Side, int From, int To)[] segments)
    {
        var now = _time.GetUtcNow().AddSeconds(17);
        var details = BreastFeed.Create(segments.Select(s => new BreastSegment(s.Side, now.AddMinutes(s.From), now.AddMinutes(s.To))), isCompleted: true).Value;
        entry.Revise<BreastFeed>(_ => Result<BreastFeed>.Ok(details), new HlcStamp(++_wall, 0, Device), _time.GetUtcNow());
        return details;
    }

    private CareEntry InParts((BreastSide Side, int From, int To) first, (BreastSide Side, int From, int To) second) => InParts([first, second], 0);

    private CareEntry InParts((BreastSide Side, int From, int To) first, (BreastSide Side, int From, int To) second, (BreastSide Side, int From, int To) third, int seconds = 0) =>
        InParts([first, second, third], seconds);

    private CareEntry InParts((BreastSide Side, int From, int To)[] segments, int seconds)
    {
        var now = _time.GetUtcNow().AddSeconds(seconds);
        var details = BreastFeed.Create(segments.Select(s => new BreastSegment(s.Side, now.AddMinutes(s.From), now.AddMinutes(s.To))), isCompleted: true).Value;
        var entry = CareEntry.Create(new EntryId(Guid.CreateVersion7(_time.GetUtcNow())), Household, EntrySubject.Baby(Baby), details, null,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), _time.GetUtcNow()).Value;
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(_ => new LiveEntry(entry));
        return entry;
    }

    private EditViewModel Model() => Model(out _);

    private EditViewModel Model(out UndoService undo)
    {
        undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), new UndoFeedMerge(_store, _clock, _time), _store, _time);
        var clockFormat = Substitute.For<IClockFormat>();
        clockFormat.Is24Hour.Returns(true);
        return new EditViewModel(_store, _households, new LogEntry(_store, _clock, _time), new ReviseEntry(_store, _clock, _time), undo, clockFormat, _time);
    }

    /// <summary>The store as SQLite is: each read is a fresh copy, each save replaces what is stored. Returns what is stored now.</summary>
    private Func<CareEntry> Stored(CareEntry entry)
    {
        var current = entry;
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(_ => new LiveEntry(Copy(current)));
        _store.When(s => s.SaveAsync(Arg.Is<CareEntry>(e => e.Id == entry.Id), Arg.Any<CancellationToken>()))
            .Do(call => current = Copy(call.Arg<CareEntry>()));
        return () => current;
    }

    private CareEntry Copy(CareEntry e) =>
        CareEntry.Create(e.Id, e.HouseholdId, e.Subject, e.Details, e.Kind.TimingFor(e.Details) == TimingRule.DerivedFromDetails ? null : e.Timing,
            e.LoggedBy, e.Source, e.Version, _time.GetUtcNow(), e.Note).Value;

    private CareEntry Diaper() =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(_time.GetUtcNow())), Household, EntrySubject.Baby(Baby),
            new DiaperDetails(DiaperContents.Dirty), EntryTiming.Point(_time.GetUtcNow().AddMinutes(-5)), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), _time.GetUtcNow()).Value;
}
