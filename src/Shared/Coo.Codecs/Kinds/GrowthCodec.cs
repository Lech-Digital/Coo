using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Growth;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;

namespace Coo.Codecs.Kinds;

/// <summary><c>growth</c> v1.</summary>
internal sealed class GrowthCodec : EntryDetailsCodec<GrowthDetails, GrowthDetailsV1>
{
    public static readonly GrowthCodec Instance = new();

    private GrowthCodec()
    {
    }

    public override EntryKindCode Kind => GrowthKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<GrowthDetailsV1> TypeInfo => CodecJson.Context.GrowthDetailsV1;

    protected override GrowthDetailsV1 ToDto(GrowthDetails details) =>
        new() { V = 1, WeightG = details.WeightGrams, LengthMm = details.LengthMm, HeadMm = details.HeadMm };

    protected override DecodeOutcome FromDto(GrowthDetailsV1 dto)
    {
        var details = GrowthDetails.Create(dto.WeightG, dto.LengthMm, dto.HeadMm);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("$"));
    }
}
