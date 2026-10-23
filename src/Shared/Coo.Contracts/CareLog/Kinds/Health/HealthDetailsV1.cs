using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.Health;

/// <summary><c>medicine</c> v1: a name and an optional free-text dose.</summary>
public sealed record MedicineDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required string Name { get; init; }

    public string? Dose { get; init; }
}

/// <summary><c>temperature</c> v1: degrees Celsius, one decimal.</summary>
public sealed record TemperatureDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required decimal Celsius { get; init; }
}

/// <summary><c>vaccine</c> v1: the vaccine's name.</summary>
public sealed record VaccineDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required string Name { get; init; }
}

/// <summary><c>visit</c> v1: who was seen, optionally.</summary>
public sealed record VisitDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public string? With { get; init; }
}
