using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Feeding;

/// <summary>
/// <c>feeding</c> v1 (Spec 003 §4.1). One flat shape for both methods; the codec checks that each method
/// carries only its own properties. Enum values are strings, so an unknown value reaches the codec.
/// </summary>
public sealed record FeedingDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    /// <summary><c>"breast"</c> or <c>"bottle"</c>.</summary>
    public required string Method { get; init; }

    public bool? Completed { get; init; }

    public IReadOnlyList<BreastSegmentV1>? Segments { get; init; }

    public IReadOnlyList<BottleComponentV1>? Components { get; init; }
}

public sealed record BreastSegmentV1
{
    /// <summary><c>"left"</c> or <c>"right"</c>.</summary>
    public required string Side { get; init; }

    public required DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }
}

public sealed record BottleComponentV1
{
    /// <summary><c>"expressedBreastMilk"</c> or <c>"formula"</c>.</summary>
    public required string Milk { get; init; }

    public required decimal VolumeMl { get; init; }
}
