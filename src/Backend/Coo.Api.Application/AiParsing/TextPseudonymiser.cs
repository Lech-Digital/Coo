using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Coo.Api.Application.AiParsing;

/// <summary>A name the household uses, and what it becomes in the text the model sees.</summary>
public sealed record KnownName(string Name, string Placeholder);

/// <summary>
/// Hides the household's names and anything that looks like contact or ID data before text leaves the server (ADR 0005
/// §4a). Names match without case or diacritics and with an inflected ending (Polish cases: Zosia, Zosi, Zosię; the
/// German genitive -s). The mapping lives only for the request, to put the names back into what is shown.
/// </summary>
public static partial class TextPseudonymiser
{
    /// <summary>How many letters an inflected ending may add or change after the name's stem.</summary>
    private const int MaxSuffix = 3;

    public static string Pseudonymise(string text, IEnumerable<KnownName> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        var result = Email().Replace(text, "[email]");
        result = Url().Replace(result, "[link]");
        // Unbroken runs first (an ID number), then spaced or dashed ones (a phone number).
        result = LongNumber().Replace(result, "[number]");
        result = Phone().Replace(result, "[phone]");

        // Longest names first, so "Anna Maria" is not split into "Anna" and "Maria".
        foreach (var name in names.Where(n => n.Name.Trim().Length >= 2).OrderByDescending(n => n.Name.Length))
            result = ReplaceName(result, name);
        return result;
    }

    /// <summary>Puts the names back into text the model returned (a source fragment), for the person to read.</summary>
    public static string Restore(string text, IEnumerable<KnownName> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names.GroupBy(n => n.Placeholder).Select(g => g.First()))
            text = text.Replace(name.Placeholder, name.Name, StringComparison.Ordinal);
        return text;
    }

    private static string ReplaceName(string text, KnownName name)
    {
        var folded = Fold(text);
        var stem = Stem(Fold(name.Name.Trim()));
        var pattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(stem)}\p{{L}}{{0,{MaxSuffix}}}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));

        // Folding keeps one character per character, so positions in the folded text are positions in the original.
        var builder = new StringBuilder();
        var last = 0;
        foreach (Match match in pattern.Matches(folded))
        {
            builder.Append(text, last, match.Index - last).Append(name.Placeholder);
            last = match.Index + match.Length;
        }

        return builder.Append(text, last, text.Length - last).ToString();
    }

    /// <summary>
    /// The part of a name every inflected form shares: "Zosia" → "Zos", "Lena" → "Len", "Leon" → "Leon". Never shorter than
    /// three letters ("Ania" → "Ani", not "An", which would catch "and").
    /// </summary>
    private static string Stem(string name)
    {
        var trimmed = name.TrimEnd('a', 'e', 'i', 'o', 'u', 'y');
        if (trimmed.Length >= 3)
            return trimmed;
        return name.Length > 3 ? name[..^1] : name;
    }

    /// <summary>Lower case without diacritics, one character for one character ("Zosię" → "zosie", "Łucja" → "lucja").</summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            var mapped = lower switch
            {
                'ł' => 'l',
                'ø' => 'o',
                'đ' => 'd',
                'ß' => 's',
                _ => BaseLetter(lower),
            };
            builder.Append(mapped);
        }

        return builder.ToString();
    }

    private static char BaseLetter(char c)
    {
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (var part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
                return part;
        }

        return c;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Url();

    /// <summary>Nine digits or more, spaced or dashed: "+31 6 1234 5678". Times and amounts ("14 30 120") stay.</summary>
    [GeneratedRegex(@"\+?\d(?:[\s-]?\d){8,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Phone();

    /// <summary>Six digits or more: PESEL, card or insurance numbers. Care amounts are never this long.</summary>
    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex LongNumber();
}
