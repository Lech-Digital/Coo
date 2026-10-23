using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Coo.Mobile.Application.Preferences;
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
            var few = Strings.Plural("BringOver.Entries", 3, "Lena");

            // Assert
            few.Should().Be("Na tym telefonie są 3 wpisy dla: Lena.");
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
    [InlineData("entry.timing-shape-not-allowed")]
    public void Error_CodesTheUiCanShow_HaveAString(string code)
    {
        // Arrange
        var generic = Strings.Get("Error.Generic");

        // Act
        var text = Strings.Error(code);

        // Assert
        text.Should().NotBe(generic, $"'{code}' needs its own message");
    }

    public static TheoryData<string, string> HouseholdWords => new()
    {
        { "en", "household" }, { "de", "haushalt" }, { "fr", "foyer|ménage" }, { "es", "hogar" }, { "it", "nucleo" },
        { "pl", "gospodarstw" }, { "nl", "huishoud" },
    };

    [Theory]
    [MemberData(nameof(HouseholdWords))]
    public void Values_EveryLanguage_SayFamilyNeverHousehold(string language, string forbidden)
    {
        // Arrange: Spec 011b §2 and R10.
        var set = Set(language);

        // Act
        var offending = set.Where(e => Regex.IsMatch(e.Value, forbidden, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(e => e.Key).ToList();

        // Assert
        offending.Should().BeEmpty();
    }

    [Theory]
    [InlineData("invitation.invalid")]
    [InlineData("invitation.role-unsupported")]
    [InlineData("invitation.too-many")]
    [InlineData("household.forbidden")]
    [InlineData("household.member-not-found")]
    [InlineData("household.owner-cannot-leave")]
    [InlineData("household.last-family")]
    [InlineData("household.confirmation-required")]
    [InlineData("household.id-taken")]
    [InlineData("household.already-has-family")]
    [InlineData("household.malformed")]
    [InlineData("auth.rate-limited")]
    [InlineData("sync.consent-required")]
    public void Error_EveryCodeThePhoneCanReceive_HasAString(string code)
    {
        // Arrange: ADR 0006 §3, spec §10.1.
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

/// <summary>Spec 011b §3: the invitation's share text. Sets the language, so it runs alone (StringsState).</summary>
[Collection(nameof(StringsState))]
public sealed class ShareTextTests : IDisposable
{
    public ShareTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Format_ShareText_PutsTheLinkCodeAndExpiryInPlace()
    {
        // Arrange
        const string link = "https://api.coo.lechdigital.nl/i/ABCDEFGH";

        // Act
        var text = Strings.Format("Invite.ShareText", link, "ABCD-EFGH", "Thu 9 Oct, 14:02");

        // Assert
        text.Should().Be(
            "Join my family on Coo, so we can keep our baby's log together: https://api.coo.lechdigital.nl/i/ABCDEFGH\n\n"
            + "If the link doesn't open Coo, go to Settings → Family → Join a family in Coo and enter the code ABCD-EFGH. "
            + "It can be used once, until Thu 9 Oct, 14:02.");
    }
}
