using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.CareLog.Kinds.Bath;
using Coo.Contracts.CareLog.Kinds.Development;
using Coo.Contracts.CareLog.Kinds.Diaper;
using Coo.Contracts.CareLog.Kinds.Feeding;
using Coo.Contracts.CareLog.Kinds.Growth;
using Coo.Contracts.CareLog.Kinds.Health;
using Coo.Contracts.CareLog.Kinds.Pump;
using Coo.Contracts.CareLog.Kinds.Sleep;
using Coo.Contracts.CareLog.Kinds.SleepPlan;

namespace Coo.Codecs;

/// <summary>
/// Source-generated, so the trimmed Android build keeps it (ADR 0010 §4). Every kind adds its DTOs here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(FeedingDetailsV1))]
[JsonSerializable(typeof(DiaperDetailsV1))]
[JsonSerializable(typeof(SleepDetailsV1))]
[JsonSerializable(typeof(PumpDetailsV1))]
[JsonSerializable(typeof(GrowthDetailsV1))]
[JsonSerializable(typeof(MedicineDetailsV1))]
[JsonSerializable(typeof(TemperatureDetailsV1))]
[JsonSerializable(typeof(VaccineDetailsV1))]
[JsonSerializable(typeof(VisitDetailsV1))]
[JsonSerializable(typeof(BathDetailsV1))]
[JsonSerializable(typeof(TummyDetailsV1))]
[JsonSerializable(typeof(SolidsDetailsV1))]
[JsonSerializable(typeof(MilestoneDetailsV1))]
[JsonSerializable(typeof(SleepPlanDetailsV1))]
internal sealed partial class CodecJsonContext : JsonSerializerContext;

internal static class CodecJson
{
    /// <summary>
    /// The writer options. Relaxed escaping keeps offsets readable (<c>+02:00</c>, not <c>+02:00</c>);
    /// these documents are stored and synced, never embedded in HTML.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(CodecJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static CodecJsonContext Context { get; } = new(Options);
}
