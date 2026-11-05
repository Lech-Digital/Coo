using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.MilkStash;

/// <summary>
/// <c>milkbag</c> v1: a bag of expressed milk in the family's stash. Volume in millilitres; where it is kept
/// (<c>"fridge"</c> or <c>"freezer"</c>); when it was thawed (Unix milliseconds, UTC); the days it was used or thrown
/// away (<c>yyyy-MM-dd</c>); the parent's label; and the number on the container.
/// </summary>
public sealed record MilkBagDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required decimal Ml { get; init; }

    public required string Place { get; init; }

    public long? ThawedAtMs { get; init; }

    public string? UsedOn { get; init; }

    public string? DiscardedOn { get; init; }

    public string? Label { get; init; }

    /// <summary>The number on the container, 1 to 999.</summary>
    public int? Number { get; init; }

    /// <summary>
    /// What the milk is in: <c>"bottle"</c> or <c>"container"</c>; absent for a bag (additive since 1.11, ADR 0010 §3.2:
    /// every earlier document reads as a bag).
    /// </summary>
    public string? Container { get; init; }
}
