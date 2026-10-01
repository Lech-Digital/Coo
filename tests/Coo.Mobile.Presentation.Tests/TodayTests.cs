using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class TodayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 3, 12, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public TodayTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task StartBreastAsync_RightSide_SavesAnOpenFeedOnTheRight()
    {
        // Arrange
        var actions = Actions();
        CareEntry? saved = null;
        await _store.SaveAsync(Arg.Do<CareEntry>(e => saved = e), Arg.Any<CancellationToken>());

        // Act
        await actions.StartBreastAsync(BreastSide.Right);

        // Assert
        var feed = saved!.Details.Should().BeOfType<BreastFeed>().Subject;
        feed.LastSide.Should().Be(BreastSide.Right);
        saved.Timing.IsInProgress.Should().BeTrue();
        saved.Source.Should().Be(EntrySource.Timer);
    }

    [Fact]
    public void Update_NightWithRunningFeed_ShowsTimerFactsAndQuickRow()
    {
        // Arrange
        var running = Entry(BreastFeed.Start(BreastSide.Right, Now.AddMinutes(-8)));
        var section = new NightSection(Actions());
        var summary = new TodaySummary([running], running, null, null, null, Now.AddHours(-1), [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));

        // Act
        section.Update(Snapshot(summary, PaletteKind.Night));

        // Assert
        section.Timer!.Digits.Should().Be("08:00");
        section.Facts.Should().HaveCount(3);
        section.Quick.Select(q => q.KindCode).Should().Contain(["diaper", "sleep", "feeding"]);
        section.AppliesTo(HomeStage.Born, PaletteKind.Night).Should().BeTrue();
        section.AppliesTo(HomeStage.Born, PaletteKind.Day).Should().BeFalse();
    }

    [Fact]
    public void Update_ExpectedBabyDueInSixWeeks_ShowsTheCountdown()
    {
        // Arrange
        var expected = BabyProfile.Create(Baby, Household, "Baby", [], null, new DateOnly(2026, 11, 12), new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var section = new DueDateSection(Actions());

        // Act
        section.Update(new HomeSnapshot(Settings(), expected, null, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres)));

        // Assert
        section.Headline.Should().Be("Due in 6 weeks");
        section.AppliesTo(HomeStage.Expecting, PaletteKind.Night).Should().BeTrue();
        section.AppliesTo(HomeStage.Born, PaletteKind.Day).Should().BeFalse();
    }

    [Fact]
    public void Duration_MinutesAndHours_ReadAsDesigned()
    {
        // Arrange
        var short1 = TimeSpan.FromMinutes(45);
        var long1 = TimeSpan.FromMinutes(80);

        // Act
        var (a, b) = (Formats.Duration(short1), Formats.Duration(long1));

        // Assert
        (a, b).Should().Be(("45 m", "1 h 20 m"));
    }

    [Fact]
    public void Volume_OuncesHousehold_ShowsOunces()
    {
        // Arrange
        var formats = new Formats(true, VolumeUnit.Ounces);

        // Act
        var text = formats.Volume(Volume.FromOunces(4).Value);

        // Assert
        text.Should().Be("4 oz");
    }

    private HomeActions Actions()
    {
        var time = new FixedTime(Now);
        return new HomeActions(new LogEntry(_store, _clock, time), new ReviseEntry(_store, _clock, time), time)
        {
            Context = new HomeContext(Household, Baby),
        };
    }

    private CareEntry Entry(EntryDetails details) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby), details, null,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(++_wall, 0, Device), Now).Value;

    private static HouseholdSettings Settings() =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;

    private static HomeSnapshot Snapshot(TodaySummary summary, PaletteKind palette)
    {
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        return new HomeSnapshot(Settings(), baby, summary, Now, palette, new Formats(true, VolumeUnit.Millilitres));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
