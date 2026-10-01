using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Localization;

/// <summary>A language choice in the list (Spec 009 §3.5).</summary>
public sealed record LanguageOption(LanguagePreference Preference, string Label);

/// <summary>Which language applies, as a pure function (ADR 0006 §1): the choice if Coo has it, else System, else English.</summary>
public static class LanguageRule
{
    /// <summary>Each language named in its own language, in <see cref="Strings.Supported"/> order.</summary>
    private static readonly Dictionary<string, string> Endonyms = new()
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["pl"] = "Polski",
        ["nl"] = "Nederlands",
    };

    public static CultureInfo Resolve(LanguagePreference preference, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(system);
        if (preference.Language is { } chosen && Strings.Supported.Contains(chosen))
            return CultureInfo.GetCultureInfo(chosen);
        return Strings.Supported.Contains(system.TwoLetterISOLanguageName) ? system : CultureInfo.GetCultureInfo("en");
    }

    public static IReadOnlyList<LanguageOption> Options(CultureInfo system)
    {
        var resolved = Resolve(LanguagePreference.System, system).TwoLetterISOLanguageName;
        return
        [
            new(LanguagePreference.System, Strings.Format("Settings.LanguageSystem", Endonyms[resolved])),
            .. Strings.Supported.Select(code => new LanguageOption(new LanguagePreference(code), Endonyms[code])),
        ];
    }

    /// <summary>The Settings row: the chosen language's name, or "System (…)".</summary>
    public static string Label(LanguagePreference preference, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(preference);
        return preference.Language is { } chosen && Endonyms.TryGetValue(chosen, out var name) ? name : Options(system)[0].Label;
    }
}
