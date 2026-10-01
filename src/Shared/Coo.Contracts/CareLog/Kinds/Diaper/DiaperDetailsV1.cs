using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Diaper;

/// <summary><c>diaper</c> v1 (Spec 003 §4.2).</summary>
public sealed record DiaperDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    /// <summary><c>"wet"</c>, <c>"dirty"</c> or <c>"mixed"</c>.</summary>
    public required string Contents { get; init; }
}
