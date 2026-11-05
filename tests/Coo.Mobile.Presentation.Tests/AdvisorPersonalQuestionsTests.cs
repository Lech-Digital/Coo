using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Advisor;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The Advisor's questions from her own log: what changed this week against the one before.</summary>
[Collection(nameof(StringsState))]
public sealed class AdvisorPersonalQuestionsTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    public AdvisorPersonalQuestionsTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static List<DayCare> Week(int daysAgo, double sleepHours, double longestHours, int feeds, int wet = 6, int days = 7) =>
        [.. Enumerable.Range(0, days).Select(i => new DayCare(
            Today.AddDays(-daysAgo - i), true, TimeSpan.FromHours(sleepHours), 6, TimeSpan.FromHours(longestHours), feeds, feeds, TimeSpan.Zero, 0,
            wet + 2, wet, 2, 0))];

    [Fact]
    public void PersonalQuestions_ShorterNightsAndMoreFeeds_AsksAboutBothBiggestFirst()
    {
        // Arrange: the longest sleep 2 h shorter, and 3 more feeds a day.
        var now = Week(1, 14, 3, 11);
        var before = Week(8, 14.5, 5, 8);

        // Act
        var questions = AdvisorKnowledgeBase.PersonalQuestions("Lena", 60, now, before);

        // Assert
        questions.Should().Equal(
            "The longest sleep of Lena got shorter this week. Why, and what can help?",
            "Lena is feeding more often this week. Is it a growth spurt?");
    }

    [Fact]
    public void PersonalQuestions_ThreeLoggedDays_AsksNothing()
    {
        // Arrange
        var now = Week(1, 10, 2, 14, days: 3);
        var before = Week(8, 15, 5, 8);

        // Act
        var questions = AdvisorKnowledgeBase.PersonalQuestions("Lena", 60, now, before);

        // Assert
        questions.Should().BeEmpty();
    }

    [Fact]
    public void PersonalQuestions_FewWetDiapersWithoutLastWeek_StillAsks()
    {
        // Arrange: nothing logged the week before; three wet diapers a day at two months.
        var now = Week(1, 15, 5, 8, wet: 3);

        // Act
        var questions = AdvisorKnowledgeBase.PersonalQuestions("Lena", 60, now, []);

        // Assert
        questions.Should().ContainSingle().Which.Should().Contain("wet diapers");
    }
}
