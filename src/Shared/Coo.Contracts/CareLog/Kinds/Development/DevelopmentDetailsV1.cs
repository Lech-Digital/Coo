using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Development;

/// <summary><c>tummy</c> v1: no fields beyond the version; the session says how long.</summary>
public sealed record TummyDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }
}

/// <summary><c>solids</c> v1: the food, an optional free-text amount, and how it went.</summary>
public sealed record SolidsDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required string Food { get; init; }

    public string? Amount { get; init; }

    /// <summary><c>"none"</c>, <c>"liked"</c>, <c>"disliked"</c> or <c>"reaction"</c>.</summary>
    public required string Reaction { get; init; }
}

/// <summary><c>milestone</c> v1: a preset code (<c>"first-smile"</c>) or <c>"custom"</c>, and an optional title.</summary>
public sealed record MilestoneDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required string Code { get; init; }

    public string? Title { get; init; }
}
