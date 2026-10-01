using System.Globalization;
using System.Resources;

namespace Coo.Mobile.Presentation.Localization;

/// <summary>
/// The app's strings in the current UI culture (ADR 0006 §2), from <c>Resources/AppResources*.resx</c>. Plurals pick a
/// CLDR category (ADR 0006 §4). Sentences are always one resource with placeholders, never concatenated.
/// </summary>
public static class Strings
{
    public static readonly string[] Supported = ["en", "de", "fr", "es", "it", "pl", "nl"];

    private static readonly ResourceManager Resources =
        new("Coo.Mobile.Presentation.Resources.AppResources", typeof(Strings).Assembly);

    /// <summary>
    /// The language in effect: the OS language if Coo supports it, English otherwise (ADR 0006 §1). Background code
    /// asks this rather than assuming the app has set a culture.
    /// </summary>
    public static CultureInfo Culture
    {
        get
        {
            var ui = CultureInfo.CurrentUICulture;
            return Supported.Contains(ui.TwoLetterISOLanguageName) ? ui : CultureInfo.GetCultureInfo("en");
        }
    }

    public static string Language => Culture.TwoLetterISOLanguageName;

    public static string Get(string key) =>
        Resources.GetString(key, Culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;

    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    /// <summary>A plural string: <c>Plural("Due.InWeeks", 3)</c> reads <c>Due.InWeeks.other</c> in English.</summary>
    public static string Plural(string key, long n) =>
        string.Format(Culture, Get($"{key}.{PluralRules.Select(Language, n)}"), n);

    /// <summary>The localized message for a Domain or Application error code, or a generic one.</summary>
    public static string Error(string? code)
    {
        if (code is null)
            return Get("Error.Generic");
        var text = Resources.GetString("Error." + code, Culture);
        return text ?? Get("Error.Generic");
    }
}
