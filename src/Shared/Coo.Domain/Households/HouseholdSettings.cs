using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Households;

public enum VolumeUnit
{
    Millilitres,
    Ounces,
}

/// <summary>
/// One per household; its ID is the household's ID, so concurrent creations converge by last-writer-wins
/// (Spec 001 §5.3).
/// </summary>
public sealed class HouseholdSettings : SyncedRecord<HouseholdId>
{
    public const string TimeZoneEmpty = "settings.time-zone-empty";

    private HouseholdSettings(
        HouseholdId householdId, VolumeUnit volumeUnit, string timeZoneId, IReadOnlySet<EntryKindCode> hiddenKinds,
        HlcStamp version, bool isDeleted)
        : base(householdId, householdId, version, isDeleted)
    {
        VolumeUnit = volumeUnit;
        TimeZoneId = timeZoneId;
        HiddenKinds = hiddenKinds;
    }

    public VolumeUnit VolumeUnit { get; private set; }

    /// <summary>An IANA ID. The application resolves it; the domain only requires one.</summary>
    public string TimeZoneId { get; private set; }

    /// <summary>Kinds whose tiles and totals are hidden. Unknown codes are kept: a newer client may know them.</summary>
    public IReadOnlySet<EntryKindCode> HiddenKinds { get; private set; }

    public static Result<HouseholdSettings> Create(
        HouseholdId householdId, VolumeUnit volumeUnit, string timeZoneId, HlcStamp version,
        IEnumerable<EntryKindCode>? hiddenKinds = null) =>
        string.IsNullOrWhiteSpace(timeZoneId)
            ? Result<HouseholdSettings>.Failure(TimeZoneEmpty)
            : new HouseholdSettings(householdId, volumeUnit, timeZoneId.Trim(), ToSet(hiddenKinds), version, isDeleted: false);

    public Result SetVolumeUnit(VolumeUnit volumeUnit, HlcStamp version) =>
        Apply(version, Result.Success, () => VolumeUnit = volumeUnit);

    public Result SetTimeZone(string timeZoneId, HlcStamp version) =>
        Apply(version, Result.Check(!string.IsNullOrWhiteSpace(timeZoneId), TimeZoneEmpty), () => TimeZoneId = timeZoneId.Trim());

    public Result SetHiddenKinds(IEnumerable<EntryKindCode> hiddenKinds, HlcStamp version) =>
        Apply(version, Result.Success, () => HiddenKinds = ToSet(hiddenKinds));

    internal static HouseholdSettings Rehydrate(
        HouseholdId householdId, VolumeUnit volumeUnit, string timeZoneId, IEnumerable<EntryKindCode> hiddenKinds,
        HlcStamp version, bool isDeleted) =>
        new(householdId, volumeUnit, timeZoneId, ToSet(hiddenKinds), version, isDeleted);

    private Result Apply(HlcStamp version, Result rule, Action change)
    {
        var outcome = Result.FirstFailure(() => EnsureWritable(version), () => rule);
        if (outcome.IsFailure)
            return outcome;

        change();
        Stamp(version);
        return Result.Success;
    }

    private static HashSet<EntryKindCode> ToSet(IEnumerable<EntryKindCode>? codes) => [.. codes ?? []];
}
