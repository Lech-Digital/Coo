using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.MilkStash;

/// <summary>
/// <c>milkbag</c> v1: a bag of expressed milk in the family's stash. Volume in millilitres; where it is kept
/// (<c>"fridge"</c> or <c>"freezer"</c>); when it was thawed (Unix milliseconds, UTC); the days it was used or thrown
/// away (<c>yyyy-MM-dd</c>); and the parent's label.
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
}
