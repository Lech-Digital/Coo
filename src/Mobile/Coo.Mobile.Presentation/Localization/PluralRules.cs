namespace Coo.Mobile.Presentation.Localization;

/// <summary>CLDR plural categories for the seven languages (ADR 0006 §4).</summary>
public static class PluralRules
{
    public const string One = "one";
    public const string Few = "few";
    public const string Many = "many";
    public const string Other = "other";

    /// <summary>The categories each language's resources must define.</summary>
    public static IReadOnlyList<string> CategoriesFor(string language) => language switch
    {
        "pl" => [One, Few, Many],
        _ => [One, Other],
    };

    public static string Select(string language, long n) => language switch
    {
        // Polish: 1 karmienie · 2–4 karmienia (but 12–14 karmień) · 0, 5+ karmień.
        "pl" => n == 1 ? One : n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) ? Few : Many,

        // French: 0 and 1 are both "one".
        "fr" => n is 0 or 1 ? One : Other,
        _ => n == 1 ? One : Other,
    };
}
