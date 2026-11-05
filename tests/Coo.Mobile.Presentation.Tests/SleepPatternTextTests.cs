using Coo.Domain.Households;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The sleep pattern in one sentence.</summary>
[Collection(nameof(StringsState))]
public sealed class SleepPatternTextTests : IDisposable
{
    private static readonly Formats Clock24 = new(true, VolumeUnit.Millilitres);

    public SleepPatternTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Describe_UsualLongestSleep_SaysWhenItStartsAndHowLong()
    {
        // Arrange
        var pattern = new SleepPattern([], new TimeOnly(19, 10), TimeSpan.FromMinutes(340));

        // Act
        var text = SleepPatternViewModel.Describe(pattern, "Lena", Clock24);

        // Assert
        text.Should().Be("Lena’s longest sleep usually starts around 19:10 and lasts about 5 h 40 m.");
    }

    [Fact]
    public void Describe_TooFewDays_SaysHowManyAreNeeded()
    {
        // Arrange
        var pattern = new SleepPattern([], null, null);

        // Act
        var text = SleepPatternViewModel.Describe(pattern, "Lena", Clock24);

        // Assert
        text.Should().Contain("3 days");
    }
}
