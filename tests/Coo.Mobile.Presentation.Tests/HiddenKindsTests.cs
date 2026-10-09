using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 009 §3.2.</summary>
public sealed class HiddenKindsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();

    [Fact]
    public void IsShown_PumpHidden_IsFalseForPumpOnly()
    {
        // Arrange
        var household = Settings("pump");

        // Act
        var pump = KindVisibility.IsShown(household, "pump");
        var diaper = KindVisibility.IsShown(household, "diaper");

        // Assert
        pump.Should().BeFalse();
        diaper.Should().BeTrue();
    }

    [Fact]
    public void Update_PumpHidden_HasNoPumpTile()
    {
        // Arrange
        var section = new LogTilesSection(Actions());

        // Act
        section.Update(Snapshot(Settings("pump"), Summary([]), PaletteKind.Day));

        // Assert
        section.Tiles.Select(t => t.KindCode).Should().Equal("feeding", "diaper", "sleep");
    }

    [Fact]
    public void Update_PumpHiddenWhilePumping_KeepsTheRunningCard()
    {
        // Arrange
        var pumping = Pump(Now.AddMinutes(-5));
        var section = new RunningSection(Actions());

        // Act
        section.Update(Snapshot(Settings("pump"), Summary([pumping]), PaletteKind.Day));

        // Assert
        section.Items.Should().ContainSingle().Which.KindCode.Should().Be("pump");
    }

    [Fact]
    public void Update_NightWithDiaperHidden_HasNoDiaperFactOrButtons()
    {
        // Arrange
        var section = new NightSection(Actions());

        // Act
        section.Update(Snapshot(Settings("diaper"), Summary([]), PaletteKind.Night));

        // Assert
        section.Facts.Select(f => f.KindCode).Should().NotContain("diaper");
        section.Quick.Select(q => q.KindCode).Should().NotContain("diaper");
    }

    [Fact]
    public void Update_Night_OffersABottleThatOpensTheBottleSheet()
    {
        // Arrange: owner's finding, 2026-10-01: at night there was no way to log a bottle.
        var actions = Actions();
        HomeSheet? requested = null;
        actions.SheetRequested += (_, r) => requested = r.Sheet;
        var section = new NightSection(actions);
        section.Update(Snapshot(Settings(), Summary([]), PaletteKind.Night));
        var bottle = section.Quick.Single(q => q.Label == "Bottle");

        // Act
        bottle.Command.Execute(null);

        // Assert
        bottle.KindCode.Should().Be("feeding");
        requested.Should().Be(HomeSheet.Bottle);
    }

    [Fact]
    public void Update_Night_OffersPumpThatStartsPumping()
    {
        var actions = Actions();
        var section = new NightSection(actions);
        section.Update(Snapshot(Settings(), Summary([]), PaletteKind.Night));
        var pump = section.Quick.Single(q => q.Label == "Pump");

        // Act & Assert
        pump.KindCode.Should().Be("pump");
        section.Facts.Select(f => f.KindCode).Should().Contain("pump");
    }

    [Fact]
    public void Update_NightWithPumpHidden_HasNoPumpFactOrButtons()
    {
        var section = new NightSection(Actions());
        section.Update(Snapshot(Settings("pump"), Summary([]), PaletteKind.Night));

        section.Facts.Select(f => f.KindCode).Should().NotContain("pump");
        section.Quick.Select(q => q.KindCode).Should().NotContain("pump");
    }

    private HomeActions Actions()
    {
        var time = new FixedTime(Now);
        return new HomeActions(new LogEntry(_store, _clock, time), new ReviseEntry(_store, _clock, time), time)
        {
            Context = new HomeContext(Household, Baby),
        };
    }

    private static HouseholdSettings Settings(params string[] hidden) =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            hidden.Select(h => EntryKindCode.From(h).Value)).Value;

    private static TodaySummary Summary(IReadOnlyList<CareEntry> running) =>
        new(running, null, null, null, null, Now.AddHours(-1), [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));

    private static HomeSnapshot Snapshot(HouseholdSettings household, TodaySummary summary, PaletteKind palette)
    {
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        return new HomeSnapshot(household, baby, summary, Now, palette, new Formats(true, VolumeUnit.Millilitres));
    }

    private static CareEntry Pump(DateTimeOffset started) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(),
            EntryTiming.Session(TimeRange.Open(started)), PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(2, 0, Device), Now).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
