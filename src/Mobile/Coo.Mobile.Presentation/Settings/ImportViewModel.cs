using System.Globalization;
using Coo.Mobile.Application.Import;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>"Import from another app": the words for what a file holds, and for what was saved.</summary>
public sealed class ImportViewModel(ImportLog import)
{
    /// <summary>Larger than any baby app's export of a few years: a file over this is not one.</summary>
    public const long MaxFileBytes = 20_000_000;

    public Task<ImportPreview?> PreviewAsync(string text) => import.PreviewAsync(text, CultureInfo.CurrentCulture, CancellationToken.None);

    public Task<ImportResult> ImportAsync(ImportPreview preview) => import.ExecuteAsync(preview, CancellationToken.None);

    /// <summary>"Found 812 entries from Huckleberry, 12 Aug 2026 – 23 Oct 2026: …", or why there is nothing to import.</summary>
    public static string Describe(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Entries.Count == 0)
            return Strings.Get("Import.Nothing");
        string Date(DateTimeOffset at) => at.ToString("d MMM yyyy", Strings.Culture);
        var lines = new List<string>
        {
            Strings.Format(preview.Source.Length > 0 ? "Import.FoundFrom" : "Import.Found", preview.Entries.Count, preview.Source, Date(preview.From!.Value), Date(preview.To!.Value)),
            Strings.Format("Import.Kinds", preview.Feeds, preview.Diapers, preview.Sleeps, preview.Pumps),
        };
        if (preview.Skipped > 0)
            lines.Add(Strings.Plural("Import.Skipped", preview.Skipped));
        if (preview.NoSide > 0)
            lines.Add(Strings.Plural("Import.NoSide", preview.NoSide));
        lines.Add(Strings.Get("Import.Twice"));
        return string.Join("\n\n", lines);
    }

    public static string Describe(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = Strings.Plural("Import.Added", result.Added);
        if (result.AlreadyThere > 0)
            text += " " + Strings.Plural("Import.AlreadyThere", result.AlreadyThere);
        return text;
    }
}
