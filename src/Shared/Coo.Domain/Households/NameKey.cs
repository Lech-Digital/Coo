using System.Globalization;
using System.Text;

namespace Coo.Domain.Households;

/// <summary>
/// The single definition of "the same name" (Spec 001 §5.1): case-insensitive, diacritic-insensitive,
/// whitespace-collapsed. The pseudonymiser reuses it (ADR 0005 §4a).
/// </summary>
public static class NameKey
{
    // Letters that NFD does not decompose into a base letter plus a mark.
    private static readonly Dictionary<char, string> Undecomposable = new()
    {
        ['ł'] = "l",
        ['ø'] = "o",
        ['ß'] = "ss",
        ['đ'] = "d",
        ['æ'] = "ae",
        ['œ'] = "oe",
    };

    public static string From(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

#pragma warning disable CA1308 // A comparison key, never displayed: lower case is the conventional form.
        var lowered = name.ToLowerInvariant();
#pragma warning restore CA1308

        var builder = new StringBuilder(lowered.Length);
        var pendingSpace = false;
        foreach (var c in lowered.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (Undecomposable.TryGetValue(c, out var replacement))
                builder.Append(replacement);
            else
                builder.Append(c);
        }

        return builder.ToString();
    }
}
