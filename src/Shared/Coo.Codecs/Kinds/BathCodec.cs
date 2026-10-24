using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Bath;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Bath;

namespace Coo.Codecs.Kinds;

/// <summary><c>bath</c> v1: nothing but the version.</summary>
internal sealed class BathCodec : EntryDetailsCodec<BathDetails, BathDetailsV1>
{
    public static readonly BathCodec Instance = new();

    private BathCodec()
    {
    }

    public override EntryKindCode Kind => BathKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<BathDetailsV1> TypeInfo => CodecJson.Context.BathDetailsV1;

    protected override BathDetailsV1 ToDto(BathDetails details) => new() { V = 1 };

    protected override DecodeOutcome FromDto(BathDetailsV1 dto) => Ok(new BathDetails());
}
