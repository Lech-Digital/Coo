using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Growth;

/// <summary><c>growth</c> v1: any of weight (grams), length and head circumference (millimetres).</summary>
public sealed record GrowthDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public int? WeightG { get; init; }

    public int? LengthMm { get; init; }

    public int? HeadMm { get; init; }
}
