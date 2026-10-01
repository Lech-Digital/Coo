using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Sleep;

/// <summary><c>sleep</c> v1 (Spec 003 §4.3).</summary>
public sealed record SleepDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    /// <summary><c>"nap"</c> or <c>"night"</c>; absent when the classifier decides.</summary>
    public string? KindOverride { get; init; }
}
