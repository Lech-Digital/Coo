using System.Globalization;
using System.Resources;
using Coo.Mobile.Application.Preferences;

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

    private static LanguagePreference _preference = LanguagePreference.System;
    private static CultureInfo? _system;

    /// <summary>
    /// The language in effect (ADR 0006 §1, Spec 009 §3.5): the stored choice, else the OS language if Coo supports
    /// it, else English. Background code asks this rather than assuming the app has set a culture.
    /// </summary>
    public static CultureInfo Culture => LanguageRule.Resolve(_preference, _system ?? CultureInfo.CurrentUICulture);

    /// <summary>
    /// Set by <see cref="LanguageController"/> only. <paramref name="system"/> is Android's language as captured before
    /// Coo changed the process culture; null reads <see cref="CultureInfo.CurrentUICulture"/>.
    /// </summary>
    public static void Use(LanguagePreference preference, CultureInfo? system)
    {
        _preference = preference ?? throw new ArgumentNullException(nameof(preference));
        _system = system;
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
