using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Tour;
using FluentAssertions;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class TourViewModelTests : IDisposable
{
    private readonly ITourState _state = Substitute.For<ITourState>();

    public TourViewModelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Steps_EveryTip_HasATitleAndABodyInEveryLanguage()
    {
        // Arrange
        string[] languages = ["en", "de", "fr", "es", "it", "pl", "nl"];

        foreach (var language in languages)
        {
            // Act
            Strings.Use(new LanguagePreference(language), null);

            // Assert: a missing key reads back as the key itself.
            foreach (var step in TourViewModel.Steps)
            {
                Strings.Get($"Tour.{step.Key}.Title").Should().NotStartWith("Tour.", $"{language} has {step.Key}'s title");
                Strings.Get($"Tour.{step.Key}.Body").Should().NotStartWith("Tour.", $"{language} has {step.Key}'s body");
            }
        }
    }

    [Fact]
    public void PreviousCommand_OnTheFirstTip_StaysThere()
    {
        // Arrange
        var model = new TourViewModel(_state);

        // Act
        model.PreviousCommand.Execute(null);

        // Assert
        model.Index.Should().Be(0);
        model.CanGoBack.Should().BeFalse();
        model.Position.Should().Be($"1 of {TourViewModel.Steps.Count}");
    }

    [Fact]
    public async Task NextCommand_OnTheLastTip_MarksTheTourSeenAndFinishes()
    {
        // Arrange
        var model = new TourViewModel(_state);
        var finished = false;
        model.Finished += (_, _) => finished = true;
        for (var i = 1; i < TourViewModel.Steps.Count; i++)
            await model.NextCommand.ExecuteAsync(null);

        // Act
        var label = model.NextLabel;
        await model.NextCommand.ExecuteAsync(null);

        // Assert
        label.Should().Be("Start using Mochi");
        finished.Should().BeTrue();
        await _state.Received(1).MarkSeenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipCommand_OnAnyTip_MarksTheTourSeenAndFinishes()
    {
        // Arrange
        var model = new TourViewModel(_state) { IsReplay = true };
        var finished = false;
        model.Finished += (_, _) => finished = true;
        await model.NextCommand.ExecuteAsync(null);

        // Act
        await model.SkipCommand.ExecuteAsync(null);

        // Assert
        finished.Should().BeTrue();
        await _state.Received(1).MarkSeenAsync(Arg.Any<CancellationToken>());
    }
}
