using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>What every bring-over sheet shows of a <see cref="BringOverChoice"/>: the summary line and where each baby goes by default.</summary>
internal static class BabyMappings
{
    /// <summary>"This phone has 42 entries for Maja."</summary>
    public static string Summary(BringOverChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return Strings.Plural("BringOver.Entries", choice.Local.EntryCount, string.Join(", ", choice.Local.Babies.Select(b => b.Name)));
    }

    /// <summary>One mapping per local baby, each choosing among the family's babies and "Add as a new baby" (the last option).</summary>
    public static IReadOnlyList<BabyMapping> For(BringOverChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        IReadOnlyList<BabyOption> options = [.. choice.Family.Select(b => new BabyOption(b.Id, b.Name)), new BabyOption(null, Strings.Get("BringOver.AddNew"))];
        return [.. choice.Local.Babies.Select(b => new BabyMapping(b, options, DefaultFor(b, choice)))];
    }

    /// <summary>The same name (as NameKey compares names) is the same baby; one baby on each side is the same baby; else new.</summary>
    private static int DefaultFor(LocalBaby local, BringOverChoice choice)
    {
        var key = NameKey.From(local.Name);
        var byName = choice.Family.ToList().FindIndex(f => NameKey.From(f.Name) == key);
        if (byName >= 0)
            return byName;
        return choice.Family.Count == 1 && choice.Local.Babies.Count == 1 ? 0 : choice.Family.Count;
    }
}
