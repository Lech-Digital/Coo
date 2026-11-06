using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Advisor;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The Advisor's age guide and questions in every language the app speaks.</summary>
[Collection(nameof(StringsState))]
public sealed class AdvisorContentTests : IDisposable
{
    public static readonly TheoryData<string> Languages = new() { "pl", "de", "fr", "es", "it", "nl" };

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Theory]
    [MemberData(nameof(Languages))]
    public void GetQuestionPool_EveryAgeInEveryLanguage_IsTranslatedAndComplete(string language)
    {
        // Arrange
        int[] ages = [0, 20, 60, 100, 200, 300, 400, 600];

        foreach (var age in ages)
        {
            Strings.Use(new LanguagePreference("en"), null);
            var english = AdvisorKnowledgeBase.GetQuestionPool(age);
            var englishStage = AdvisorKnowledgeBase.GetStage(age);
            Strings.Use(new LanguagePreference(language), null);

            // Act
            var questions = AdvisorKnowledgeBase.GetQuestionPool(age);
            var stage = AdvisorKnowledgeBase.GetStage(age);

            // Assert: as many as in English, none empty, none left in English.
            questions.Should().HaveCount(english.Length);
            questions.Should().OnlyContain(q => q.Trim().Length > 0);
            questions.Intersect(english).Should().BeEmpty($"{language} at {age} days should be translated");
            stage.Summary.Should().NotBe(englishStage.Summary);
            stage.MotorSkills.Split('\n').Should().HaveCount(englishStage.MotorSkills.Split('\n').Length);
        }
    }

    [Fact]
    public void FullGuide_Polish_UsesPolishHeadings()
    {
        // Arrange
        Strings.Use(new LanguagePreference("pl"), null);
        var stage = AdvisorKnowledgeBase.GetStage(60);

        // Act
        var guide = AdvisorKnowledgeBase.FullGuide(stage);

        // Assert
        guide.Should().Contain("Rytm, sen i karmienie");
        stage.StageBadge.Should().Be("✨ 6–12 tygodni · Uśmiechy i gaworzenie");
    }

    [Fact]
    public void GetFallbackQuestions_UnknownLanguage_FallsBackToEnglish()
    {
        // Arrange: a language the app has no guide for.
        Strings.Use(new LanguagePreference("en"), null);

        // Act
        var fallback = AdvisorKnowledgeBase.GetFallbackQuestions();

        // Assert
        fallback.Should().Contain("Is 3–4 hours between daytime feeds normal?");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("nl")]
    public void GeneratedContent_EveryLanguage_MatchesItsJson(string language)
    {
        // Arrange: the JSON is the file to edit; eng/gen-advisor-content.py makes the C# from it.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Mobile", "Coo.Mobile.Presentation", "Advisor", "Content")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the tests run inside the repository");
        var file = Path.Combine(directory!.FullName, "src", "Mobile", "Coo.Mobile.Presentation", "Advisor", "Content", language + ".json");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        Strings.Use(new LanguagePreference(language), null);

        foreach (var band in json.RootElement.GetProperty("questions").EnumerateArray())
        {
            // Act
            var questions = AdvisorKnowledgeBase.GetQuestionPool(band.GetProperty("from").GetInt32());

            // Assert
            questions.Should().Equal(band.GetProperty("items").EnumerateArray().Select(q => q.GetString()), "run python3 eng/gen-advisor-content.py after editing {0}.json", language);
        }

        foreach (var stage in json.RootElement.GetProperty("stages").EnumerateArray())
            AdvisorKnowledgeBase.GetStage(stage.GetProperty("from").GetInt32()).Summary.Should().Be(stage.GetProperty("summary").GetString());
    }
}
