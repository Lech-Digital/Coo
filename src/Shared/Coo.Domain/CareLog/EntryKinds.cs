using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;

namespace Coo.Domain.CareLog;

/// <summary>
/// The catalogue of every kind this build knows (Spec 001 §7.1). A hand-written list: no DI, no reflection,
/// so trimmed Release builds keep it (ADR 0010 §4). Adding a kind means adding one line here.
/// </summary>
public static class EntryKinds
{
    public static IReadOnlyList<IEntryKind> All { get; } =
    [
        FeedingKind.Instance,
        DiaperKind.Instance,
        SleepKind.Instance,
        PumpKind.Instance,
        GrowthKind.Instance,
        MedicineKind.Instance,
        TemperatureKind.Instance,
        VaccineKind.Instance,
        VisitKind.Instance,
    ];

    public static IEntryKind? Find(EntryKindCode code) => All.FirstOrDefault(k => k.Code == code);

    /// <summary>The kind whose details type is <paramref name="detailsType"/> or one of its base types.</summary>
    public static IEntryKind? For(Type detailsType)
    {
        ArgumentNullException.ThrowIfNull(detailsType);
        return All.FirstOrDefault(k => k.DetailsType.IsAssignableFrom(detailsType));
    }
}
