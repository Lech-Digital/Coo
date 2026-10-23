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

    private static readonly AsyncLocal<CultureInfo?> Override = new();

    private static LanguagePreference _preference = LanguagePreference.System;
    private static CultureInfo? _system;

    /// <summary>
    /// The language in effect (ADR 0006 §1, Spec 009 §3.5): the stored choice, else the OS language if Coo supports
    /// it, else English. Background code asks this rather than assuming the app has set a culture.
    /// </summary>
    public static CultureInfo Culture => Override.Value ?? LanguageRule.Resolve(_preference, _system ?? CultureInfo.CurrentUICulture);

    /// <summary>
    /// Strings in another of the <see cref="Supported"/> languages until disposed, on this flow only: a report for a
    /// doctor who reads another language, while the app stays in its own. An unsupported code changes nothing.
    /// </summary>
    public static IDisposable InLanguage(string language)
    {
        var previous = Override.Value;
        if (Supported.Contains(language))
            Override.Value = CultureInfo.GetCultureInfo(language);
        return new Restore(previous);
    }

    /// <summary>The language's own name, capitalised: "Deutsch", "Polski".</summary>
    public static string NativeName(string language)
    {
        var name = CultureInfo.GetCultureInfo(language).NativeName;
        return name.Length == 0 ? language : char.ToUpper(name[0], CultureInfo.GetCultureInfo(language)) + name[1..];
    }

    private sealed class Restore(CultureInfo? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }

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

    /// <summary>
    /// A plural string: <c>Plural("Due.InWeeks", 3)</c> reads <c>Due.InWeeks.other</c> in English. <c>{0}</c> is
    /// <paramref name="n"/>, <c>{1}</c>… are <paramref name="more"/>.
    /// </summary>
    public static string Plural(string key, long n, params object[] more) =>
        string.Format(Culture, Get($"{key}.{PluralRules.Select(Language, n)}"), [n, .. more]);

    /// <summary>The localized message for a Domain or Application error code, or a generic one.</summary>
    public static string Error(string? code)
    {
        if (code is null)
            return Get("Error.Generic");
        var text = Resources.GetString("Error." + code, Culture);
        return text ?? Get("Error.Generic");
    }
}
