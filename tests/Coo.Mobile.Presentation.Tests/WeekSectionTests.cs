using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Today's "This week": what changed, the biggest first, small changes left out.</summary>
[Collection(nameof(StringsState))]
public sealed class WeekSectionTests : IDisposable
{
    private static readonly HouseholdSettings Household = HouseholdSettings.Create(
        new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), VolumeUnit.Millilitres, "Europe/Amsterdam",
        new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")))).Value;

    public WeekSectionTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void For_NapTransitionAndLongerNights_PutsTheNapsFirst()
    {
        // Arrange
        var week = new WeekNote(new WeekComparison(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(70), 0.5, 7, true, true), new NapTransition(4, 3, false));

        // Act
        var lines = WeekSection.For(week, 120, Household);

        // Assert
        lines.Select(l => l.Text).Should().Equal("Moving from 4 naps to 3", "Longest sleep 1 h 10 m longer than last week");
    }

    [Fact]
    public void For_SmallChanges_SaysNothing()
    {
        // Arrange
        var week = new WeekNote(new WeekComparison(TimeSpan.FromMinutes(-20), TimeSpan.FromMinutes(15), 1, 7, true, true), null);

        // Act
        var lines = WeekSection.For(week, 120, Household);

        // Assert
        lines.Should().BeEmpty();
    }

    [Fact]
    public void For_NoLastWeekButFewWetDiapers_StillWarns()
    {
        // Arrange
        var week = new WeekNote(new WeekComparison(TimeSpan.Zero, TimeSpan.Zero, 0, 3.5, true, false), null);

        // Act
        var lines = WeekSection.For(week, 60, Household);

        // Assert
        lines.Should().ContainSingle().Which.KindCode.Should().Be("diaper");
    }
}
