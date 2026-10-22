using Coo.Api.Application.Advisor;

namespace Coo.Api.Application.Tests;

public sealed class AdvisorPromptTests
{
    [Fact]
    public void System_WithLogAndPolish_GroundsTheAnswerInTheLogAndTheLanguage()
    {
        // Act
        var system = AdvisorPrompt.System("Maja", 9, "- Thu 22 Oct: sleep 13 h 05 m", "pl");

        // Assert
        system.Should().Contain("Maja, is 9 weeks old");
        system.Should().Contain("sleep 13 h 05 m");
        system.Should().Contain("Always answer in Polish.");
        system.Should().Contain("not a doctor");
    }

    [Fact]
    public void System_UnknownLanguageAndNoLog_FallsBackToEnglishWithoutALogSection()
    {
        // Act
        var system = AdvisorPrompt.System(null, null, null, "xx");

        // Assert
        system.Should().Contain("Always answer in English.");
        system.Should().NotContain("The baby's log");
    }

    [Fact]
    public void History_LongThread_KeepsTheLastMessagesCutToLength()
    {
        // Arrange
        var thread = Enumerable.Range(0, 30)
            .Select(i => new AdvisorMessageHistory(new string('x', 5000) + i, i % 2 == 0))
            .Append(new AdvisorMessageHistory("  ", true));

        // Act
        var history = AdvisorPrompt.History(thread);

        // Assert
        history.Should().HaveCount(AdvisorPrompt.MaxHistoryMessages);
        history.Should().OnlyContain(m => m.Text.Length == AdvisorPrompt.MaxHistoryMessageLength);
    }

    [Fact]
    public void System_HugeContext_IsCut()
    {
        // Act
        var system = AdvisorPrompt.System(null, null, new string('y', 50_000), "en");

        // Assert
        system.Length.Should().BeLessThan(AdvisorPrompt.MaxContextLength + 2000);
    }
}
