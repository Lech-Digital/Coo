using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Pump;

/// <summary><c>pump</c> v1 (Spec 003 §4.4).</summary>
public sealed record PumpDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    /// <summary>Absent until the amount is recorded.</summary>
    public PumpYieldV1? Yield { get; init; }
}

/// <summary>Sides (<see cref="LeftMl"/>, <see cref="RightMl"/>), or only <see cref="TotalMl"/>; never both.</summary>
public sealed record PumpYieldV1
{
    public decimal? LeftMl { get; init; }

    public decimal? RightMl { get; init; }

    public decimal? TotalMl { get; init; }
}
