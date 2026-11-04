using Coo.Api.Application.Advisor;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 019 §4: the plan's words follow the computed facts, and a reply that invents a time is not shown.</summary>
public sealed class SleepPlanPromptTests
{
    private const string Facts = """
        Wake-up 07:00. Nap 1 at 09:30 for about 1 h 20 m. Nap 2 at 13:15. Bedtime 19:00.
        Approach: fading.
        """;

    [Fact]
    public void System_PlanInPolish_KeepsTheRulesAndTheLanguage()
    {
        // Act
        var system = SleepPlanPrompt.System(SleepPlanWordsKind.Plan, "pl");

        // Assert
        system.Should().Contain("Always write in Polish");
        system.Should().Contain("Quote only the times and numbers in the facts");
        system.Should().Contain(SleepPlanPrompt.BabyToken, "the baby's name never leaves the phone");
        system.Should().Contain("what to expect in the first week");
        system.Should().Contain("leaving the baby to cry alone", "extinction is never offered (owner, 2026-11-03)");
    }

    [Fact]
    public void System_CheckIn_AsksForTheCheckInNoteNotThePlan()
    {
        // Act
        var system = SleepPlanPrompt.System(SleepPlanWordsKind.CheckIn, null);

        // Assert
        system.Should().Contain("check-in note");
        system.Should().NotContain("what to expect in the first week");
        system.Should().Contain("Always write in English");
    }

    [Theory]
    [InlineData("Wake {baby} at 07:00, and start the first nap around 9:30.", true)]
    [InlineData("Bedtime at 19.00 works well.", true)]
    [InlineData("No times at all, just the routine.", true)]
    [InlineData("Try a bedtime at 18:45 instead.", false)]
    [InlineData("Nap 2 at 13:15, then a short third nap at 16:30.", false)]
    public void QuotesOnlyTheFacts_TimesInTheReply_AcceptsOnlyTheFactsTimes(string reply, bool expected)
    {
        // Act
        var ok = SleepPlanPrompt.QuotesOnlyTheFacts(reply, Facts);

        // Assert
        ok.Should().Be(expected);
    }

    [Fact]
    public void Accept_EmptyOrInventedReply_IsNotShown()
    {
        // Act
        var empty = SleepPlanPrompt.Accept("   ", Facts);
        var invented = SleepPlanPrompt.Accept("Bedtime 20:30.", Facts);
        var fine = SleepPlanPrompt.Accept("  Bedtime 19:00 for {baby}.  ", Facts);

        // Assert
        empty.Should().BeNull();
        invented.Should().BeNull();
        fine.Should().Be("Bedtime 19:00 for {baby}.");
    }

    [Fact]
    public void User_HugeFacts_AreCut()
    {
        // Act
        var user = SleepPlanPrompt.User(new string('x', 50_000));

        // Assert
        user.Length.Should().BeLessThan(SleepPlanPrompt.MaxFactsLength + 100);
    }
}
