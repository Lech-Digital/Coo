namespace Coo.Mobile.Pages;

/// <summary>
/// Android's action sheet answers with the chosen label, and two families can share one ("No baby yet · Your family"). A
/// repeated label gets zero-width spaces appended, invisible but distinct, so the answer maps back to its position.
/// </summary>
internal static class ActionSheetChoices
{
    /// <returns>The chosen entry's position, or -1 for Cancel or a dismissed sheet.</returns>
    public static async Task<int> ChooseIndexAsync(Page page, string title, string cancel, IReadOnlyList<string> labels)
    {
        var shown = Distinct(labels);
        var chosen = await page.DisplayActionSheetAsync(title, cancel, null, [.. shown]);
        return chosen is null ? -1 : shown.IndexOf(chosen);
    }

    internal static List<string> Distinct(IReadOnlyList<string> labels)
    {
        var shown = new List<string>(labels.Count);
        foreach (var label in labels)
        {
            var unique = label;
            while (shown.Contains(unique))
                unique += "\u200B";
            shown.Add(unique);
        }

        return shown;
    }
}
