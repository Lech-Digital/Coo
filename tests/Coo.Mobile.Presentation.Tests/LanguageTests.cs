using System.Globalization;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class LanguageRuleTests
{
    [Theory]
    [InlineData(null, "pl-PL", "pl")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("de", "pl-PL", "de")]
    [InlineData("xx", "nl-NL", "nl")]
    public void Resolve_PreferenceAndSystem_PicksTheLanguage(string? chosen, string system, string expected)
    {
        // Arrange
        var preference = new LanguagePreference(chosen);

        // Act
        var culture = LanguageRule.Resolve(preference, CultureInfo.GetCultureInfo(system));

        // Assert
        culture.TwoLetterISOLanguageName.Should().Be(expected);
    }

    [Fact]
    public void Options_PolishPhone_StartsWithSystemNamingPolishThenTheSevenEndonyms()
    {
        // Arrange
        var system = CultureInfo.GetCultureInfo("pl-PL");

        // Act
        var options = LanguageRule.Options(system);

        // Assert
        options.Should().HaveCount(8);
        options[0].Preference.IsSystem.Should().BeTrue();
        options[0].Label.Should().Contain("Polski");
        options.Skip(1).Select(o => o.Label).Should().Equal("English", "Deutsch", "Français", "Español", "Italiano", "Polski", "Nederlands");
    }
}

[CollectionDefinition(nameof(StringsState), DisableParallelization = true)]
public sealed class StringsState;

[Collection(nameof(StringsState))]
public sealed class LanguageControllerTests : IDisposable
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private readonly IDevicePreferences _preferences = Substitute.For<IDevicePreferences>();

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task StartAsync_StoredPolish_MakesStringsPolish()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("pl"));
        var controller = new LanguageController(_preferences);

        // Act
        await controller.StartAsync(Dutch, CancellationToken.None);

        // Assert
        Strings.Language.Should().Be("pl");
    }

    [Fact]
    public async Task SetAsync_BackToSystem_UsesTheCapturedSystemCulture()
    {
        // Arrange: Polish chosen on a Dutch phone, then System again.
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("pl"));
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        await controller.SetAsync(LanguagePreference.System, CancellationToken.None);

        // Assert
        Strings.Language.Should().Be("nl");
        raised.Should().Be(1);
        await _preferences.Received(1).SetLanguageAsync(LanguagePreference.System, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemChanged_WithExplicitChoice_KeepsTheChoiceAndRaisesNothing()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("de"));
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        controller.SystemChanged(CultureInfo.GetCultureInfo("fr-FR"));

        // Assert
        Strings.Language.Should().Be("de");
        raised.Should().Be(0);
    }

    [Fact]
    public async Task SystemChanged_OnSystem_FollowsAndRaisesChanged()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(LanguagePreference.System);
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        controller.SystemChanged(CultureInfo.GetCultureInfo("fr-FR"));

        // Assert
        Strings.Language.Should().Be("fr");
        raised.Should().Be(1);
    }
}

[Collection(nameof(StringsState))]
public sealed class RunningItemLanguageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly Coo.Domain.Common.HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly Coo.Domain.Common.BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly Coo.Domain.Common.DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Update_LanguageChangedWhileFeeding_RelabelsTheTimerButtons()
    {
        // Arrange: reviewer's finding — "Switch side" and "Stop" stayed English after choosing Polski.
        Strings.Use(new LanguagePreference("en"), null);
        var feed = Coo.Domain.CareLog.CareEntry.Create(
            new Coo.Domain.Common.EntryId(Guid.CreateVersion7(Now)), Household, Coo.Domain.CareLog.EntrySubject.Baby(Baby),
            Coo.Domain.CareLog.Kinds.Feeding.BreastFeed.Start(Coo.Domain.CareLog.Kinds.Feeding.BreastSide.Left, Now.AddMinutes(-3)), null,
            Coo.Domain.CareLog.PersonRef.ThisDevicesPerson, Coo.Domain.CareLog.EntrySource.Timer, new Coo.Domain.Common.Versioning.HlcStamp(2, 0, Device), Now).Value;
        var section = new Coo.Mobile.Presentation.Today.RunningSection(Actions());
        var snapshot = Snapshot(feed);
        section.Update(snapshot);

        // Act
        Strings.Use(new LanguagePreference("pl"), null);
        section.Update(snapshot);

        // Assert
        section.Items.Single().Actions.Select(a => a.Label).Should().Equal("Pauza", "Zmień stronę", "Stop");
    }

    private static Coo.Mobile.Presentation.Today.HomeActions Actions()
    {
        var time = new FixedTime(Now);
        var store = Substitute.For<Coo.Mobile.Application.CareLog.ICareLogStore>();
        var clock = Substitute.For<Coo.Mobile.Application.CareLog.IVersionClock>();
        return new Coo.Mobile.Presentation.Today.HomeActions(
            new Coo.Mobile.Application.CareLog.LogEntry(store, clock, time), new Coo.Mobile.Application.CareLog.ReviseEntry(store, clock, time), time);
    }

    private static Coo.Mobile.Presentation.Today.HomeSnapshot Snapshot(Coo.Domain.CareLog.CareEntry running)
    {
        var household = Coo.Domain.Households.HouseholdSettings.Create(Household, Coo.Domain.Households.VolumeUnit.Millilitres, "Europe/Amsterdam", new Coo.Domain.Common.Versioning.HlcStamp(1, 0, Device)).Value;
        var baby = Coo.Domain.Households.BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new Coo.Domain.Common.Versioning.HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var summary = new Coo.Mobile.Application.CareLog.TodaySummary([running], running, null, null, null, Now.AddHours(-1), [],
            Coo.Mobile.Application.CareLog.DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));
        return new Coo.Mobile.Presentation.Today.HomeSnapshot(household, baby, summary, Now, Coo.Mobile.Presentation.Theme.PaletteKind.Day,
            new Coo.Mobile.Presentation.Formatting.Formats(true, Coo.Domain.Households.VolumeUnit.Millilitres));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
