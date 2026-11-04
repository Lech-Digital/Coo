using Coo.Api.Application.Advisor;

namespace Coo.Api.Application.Tests;

public sealed class AdvisorPromptTests
{
    [Fact]
    public void System_WithLogAndPolish_GroundsTheAnswerInTheLogAndTheLanguage()
    {
        // Act
        var system = AdvisorPrompt.System("Lena", 9, "- Thu 22 Oct: sleep 13 h 05 m", "pl", new DateTimeOffset(2026, 10, 23, 3, 10, 0, TimeSpan.FromHours(2)));

        // Assert
        system.Should().Contain("Lena, is 9 weeks old");
        system.Should().Contain("sleep 13 h 05 m");
        system.Should().Contain("Always answer in Polish");
        system.Should().Contain("Friday 03:10 (night)");
        system.Should().Contain("Do not open or close with disclaimers", "the page shows the note; the answer should not repeat it");
        system.Should().Contain("you can only help with the baby and the family", "off-topic use is declined in the same call, with no second model call");
    }

    [Fact]
    public void System_UnknownLanguageAndNoLog_FallsBackToEnglishWithoutALogSection()
    {
        // Act
        var system = AdvisorPrompt.System(null, null, null, "xx");

        // Assert
        system.Should().Contain("Always answer in English");
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
        system.Length.Should().BeLessThan(AdvisorPrompt.MaxContextLength + 4000);
    }

    [Theory]
    [InlineData(null, 17)]
    [InlineData(20, 17)]
    [InlineData(60, 55)]
    [InlineData(5, 17)]
    [InlineData(300, 55)]
    public void Deadline_WhatThePhoneWaits_LeavesAMarginButNeverLessThanToday(int? waitSeconds, int expected)
    {
        // Act
        var deadline = AdvisorPrompt.Deadline(waitSeconds);

        // Assert
        deadline.Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public void Models_PremiumTriesTheBetterModelThenEveryonesInTheSameOrder()
    {
        // Assert
        AdvisorPrompt.Models[0].Should().Be("gemini-3.1-flash-lite", "the fastest and cheapest under load on 2026-11-04");
        AdvisorPrompt.PremiumModels.Should().Equal(["gemini-3.6-flash", .. AdvisorPrompt.Models]);
        AdvisorPrompt.PremiumBetterPerMonth.Should().BeInRange(1, 60, "60 better answers at 2027 prices is about a third of a yearly Premium");
    }
}
