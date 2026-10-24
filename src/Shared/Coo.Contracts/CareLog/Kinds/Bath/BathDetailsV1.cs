using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Bath;

/// <summary><c>bath</c> v1: no fields beyond the version; the entry's time says when.</summary>
public sealed record BathDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }
}
