using System.Text;

namespace Coo.Mobile.Application.Import;

/// <summary>
/// A CSV file as rows of cells (RFC 4180, as apps write it): the delimiter guessed from the header (comma, semicolon or
/// tab), quoted cells with doubled quotes and line breaks inside, a byte-order mark ignored, blank lines skipped.
/// </summary>
public static class CsvText
{
    public static IReadOnlyList<IReadOnlyList<string>> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var delimiter = Delimiter(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(cell.ToString().Trim());
                cell.Clear();
                if (row.Any(v => v.Length > 0))
                    rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        row.Add(cell.ToString().Trim());
        if (row.Any(v => v.Length > 0))
            rows.Add(row);
        return rows;
    }

    // The header's most frequent of the three, outside quotes.
    private static char Delimiter(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        char[] candidates = [',', ';', '\t'];
        return candidates.MaxBy(d => header.Count(c => c == d));
    }
}
