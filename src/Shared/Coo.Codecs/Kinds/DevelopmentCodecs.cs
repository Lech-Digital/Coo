using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Development;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Development;

namespace Coo.Codecs.Kinds;

/// <summary><c>tummy</c> v1: nothing but the version.</summary>
internal sealed class TummyCodec : EntryDetailsCodec<TummyDetails, TummyDetailsV1>
{
    public static readonly TummyCodec Instance = new();

    private TummyCodec()
    {
    }

    public override EntryKindCode Kind => TummyKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<TummyDetailsV1> TypeInfo => CodecJson.Context.TummyDetailsV1;

    protected override TummyDetailsV1 ToDto(TummyDetails details) => new() { V = 1 };

    protected override DecodeOutcome FromDto(TummyDetailsV1 dto) => Ok(new TummyDetails());
}

/// <summary><c>solids</c> v1.</summary>
internal sealed class SolidsCodec : EntryDetailsCodec<SolidsDetails, SolidsDetailsV1>
{
    public static readonly SolidsCodec Instance = new();

    private SolidsCodec()
    {
    }

    public override EntryKindCode Kind => SolidsKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<SolidsDetailsV1> TypeInfo => CodecJson.Context.SolidsDetailsV1;

    protected override SolidsDetailsV1 ToDto(SolidsDetails details) => new()
    {
        V = 1,
        Food = details.Food,
        Amount = details.Amount,
        Reaction = details.Reaction switch
        {
            SolidsReaction.Liked => "liked",
            SolidsReaction.Disliked => "disliked",
            SolidsReaction.Reaction => "reaction",
            _ => "none",
        },
    };

    protected override DecodeOutcome FromDto(SolidsDetailsV1 dto)
    {
        SolidsReaction? reaction = dto.Reaction switch
        {
            "none" => SolidsReaction.None,
            "liked" => SolidsReaction.Liked,
            "disliked" => SolidsReaction.Disliked,
            "reaction" => SolidsReaction.Reaction,
            _ => null,
        };
        if (reaction is null)
            return new Malformed(DecodeReasons.Invalid("reaction"));
        var details = SolidsDetails.Create(dto.Food, dto.Amount, reaction.Value);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("food"));
    }
}

/// <summary><c>milestone</c> v1. A code this version does not know is malformed: presets only grow with a new version.</summary>
internal sealed class MilestoneCodec : EntryDetailsCodec<MilestoneDetails, MilestoneDetailsV1>
{
    public static readonly MilestoneCodec Instance = new();

    private MilestoneCodec()
    {
    }

    public override EntryKindCode Kind => MilestoneKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<MilestoneDetailsV1> TypeInfo => CodecJson.Context.MilestoneDetailsV1;

    protected override MilestoneDetailsV1 ToDto(MilestoneDetails details) => new() { V = 1, Code = details.Code, Title = details.Title };

    protected override DecodeOutcome FromDto(MilestoneDetailsV1 dto)
    {
        var details = MilestoneDetails.Create(dto.Code, dto.Title);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("code"));
    }
}
