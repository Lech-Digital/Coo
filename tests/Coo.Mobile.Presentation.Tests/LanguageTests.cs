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
