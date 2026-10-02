using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Completeness is a test, not a process (ADR 0006 §3).</summary>
public sealed partial class LocalizationTests
{
    private static readonly ResourceManager Resources =
        new("Coo.Mobile.Presentation.Resources.AppResources", typeof(Strings).Assembly);

    public static TheoryData<string> Translations => ["de", "fr", "es", "it", "pl", "nl"];

    [Theory]
    [MemberData(nameof(Translations))]
    public void Keys_EveryLanguage_HasEveryNonPluralKeyAndMatchingPlaceholders(string language)
    {
        // Arrange
        var english = Set("en");
        var translated = Set(language);

        // Act
        var problems = english
            .Where(e => !IsPlural(e.Key))
            .Select(e => (e.Key, English: e.Value, Text: translated.GetValueOrDefault(e.Key)))
            .Where(r => string.IsNullOrWhiteSpace(r.Text) || !Placeholders(r.English).SetEquals(Placeholders(r.Text!)))
            .Select(r => r.Key)
            .ToList();

        // Assert
        problems.Should().BeEmpty($"every string needs a {language} translation with the same placeholders");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("pl")]
    [InlineData("nl")]
    public void Keys_EveryPluralKey_HasTheLanguagesCategories(string language)
    {
        // Arrange
        var set = Set(language);
        var bases = Set("en").Keys.Where(IsPlural).Select(k => k[..k.LastIndexOf('.')]).Distinct();

        // Act
        var missing = bases
            .SelectMany(b => PluralRules.CategoriesFor(language).Select(c => $"{b}.{c}"))
            .Where(k => string.IsNullOrWhiteSpace(set.GetValueOrDefault(k)))
            .ToList();

        // Assert
        missing.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 2, "few")]
    [InlineData("pl", 4, "few")]
    [InlineData("pl", 5, "many")]
    [InlineData("pl", 12, "many")]
    [InlineData("pl", 14, "many")]
    [InlineData("pl", 22, "few")]
    [InlineData("pl", 25, "many")]
    [InlineData("pl", 0, "many")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 1, "one")]
    [InlineData("fr", 2, "other")]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("de", 2, "other")]
    public void Select_CldrExamples_PickTheRightCategory(string language, long n, string expected)
    {
        // Arrange
        var lang = language;

        // Act
        var category = PluralRules.Select(lang, n);

        // Assert
        category.Should().Be(expected);
    }

    [Fact]
    public void Plural_PolishFeeds_ReadNaturally()
    {
        // Arrange
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl");

        try
        {
            // Act
            var two = Strings.Plural("Due.InWeeks", 2);
            var five = Strings.Plural("Due.InWeeks", 5);

            // Assert
            two.Should().Be("Termin za 2 tygodnie");
            five.Should().Be("Termin za 5 tygodni");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Plural_WithAnExtraArgument_FillsBothPlaceholders()
    {
        // Arrange
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl");

        try
        {
            // Act
            var few = Strings.Plural("BringOver.Entries", 3, "Maja");

            // Assert
            few.Should().Be("Na tym telefonie są 3 wpisy dla: Maja.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Culture_UnsupportedLanguage_FallsBackToEnglish()
    {
        // Arrange
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");

        try
        {
            // Act
            var text = Strings.Get("Tab.Today");

            // Assert
            text.Should().Be("Today");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("baby.name-invalid")]
    [InlineData("baby.birth-in-future")]
    [InlineData("baby.due-date-out-of-range")]
    [InlineData("baby.birth-or-due-date-required")]
    [InlineData("baby.already-born")]
    public void Error_CodesTheUiCanShow_HaveAString(string code)
    {
        // Arrange
        var generic = Strings.Get("Error.Generic");

        // Act
        var text = Strings.Error(code);

        // Assert
        text.Should().NotBe(generic, $"'{code}' needs its own message");
    }

    private static bool IsPlural(string key) => key.EndsWith(".one", StringComparison.Ordinal) || key.EndsWith(".other", StringComparison.Ordinal)
        || key.EndsWith(".few", StringComparison.Ordinal) || key.EndsWith(".many", StringComparison.Ordinal);

    private static Dictionary<string, string> Set(string language)
    {
        var culture = language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {language}.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
