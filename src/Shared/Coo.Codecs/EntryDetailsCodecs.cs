using Coo.Codecs.Kinds;
using Coo.Domain.CareLog;

namespace Coo.Codecs;

/// <summary>
/// Every kind's codec, as a hand-written list like <see cref="EntryKinds"/> (Spec 003 §5). A test fails if a
/// kind has no codec, or a codec no kind.
/// </summary>
public static class EntryDetailsCodecs
{
    public static IReadOnlyList<IEntryDetailsCodec> All { get; } =
    [
        FeedingCodec.Instance,
        DiaperCodec.Instance,
        SleepCodec.Instance,
        PumpCodec.Instance,
        GrowthCodec.Instance,
        MedicineCodec.Instance,
        TemperatureCodec.Instance,
        VaccineCodec.Instance,
        VisitCodec.Instance,
        BathCodec.Instance,
        TummyCodec.Instance,
        SolidsCodec.Instance,
        MilestoneCodec.Instance,
        SleepPlanCodec.Instance,
    ];

    public static IEntryDetailsCodec? For(EntryKindCode kind) => All.FirstOrDefault(c => c.Kind == kind);

    public static IEntryDetailsCodec For(EntryDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var kind = EntryKinds.For(details.GetType())
            ?? throw new ArgumentException($"{details.GetType().Name} belongs to no kind.", nameof(details));
        return For(kind.Code) ?? throw new InvalidOperationException($"Kind '{kind.Code}' has no codec.");
    }
}
