using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Pump;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Pump;

namespace Coo.Codecs.Kinds;

/// <summary><c>pump</c> v1 (Spec 003 §4.4). A total is never stored alongside sides: it is derived.</summary>
internal sealed class PumpCodec : EntryDetailsCodec<PumpDetails, PumpDetailsV1>
{
    public static readonly PumpCodec Instance = new();

    private PumpCodec()
    {
    }

    public override EntryKindCode Kind => PumpKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<PumpDetailsV1> TypeInfo => CodecJson.Context.PumpDetailsV1;

    protected override PumpDetailsV1 ToDto(PumpDetails details) => new()
    {
        V = 1,
        Yield = details.Yield switch
        {
            null => null,
            PumpYieldBySide sides => new PumpYieldV1
            {
                LeftMl = sides.Left is { } left ? Canonical(left.Millilitres) : null,
                RightMl = sides.Right is { } right ? Canonical(right.Millilitres) : null,
            },
            PumpYieldTotal total => new PumpYieldV1 { TotalMl = Canonical(total.Amount.Millilitres) },
            _ => throw new ArgumentOutOfRangeException(nameof(details), details.Yield.GetType().Name, "Unknown pump yield."),
        },
    };

    protected override DecodeOutcome FromDto(PumpDetailsV1 dto)
    {
        if (dto.Yield is not { } yield)
            return Ok(new PumpDetails());

        if (yield.TotalMl is { } totalMl)
        {
            if (yield.LeftMl is not null || yield.RightMl is not null)
                return new Malformed(DecodeReasons.Unexpected("totalMl"));

            var total = Volume.FromMillilitres(totalMl);
            return total.IsSuccess ? Ok(new PumpDetails(PumpYield.OfTotal(total.Value))) : new Malformed(total.Error!.Code);
        }

        Volume? left = null, right = null;
        if (yield.LeftMl is { } leftMl)
        {
            var volume = Volume.FromMillilitres(leftMl);
            if (volume.IsFailure)
                return new Malformed(volume.Error!.Code);
            left = volume.Value;
        }

        if (yield.RightMl is { } rightMl)
        {
            var volume = Volume.FromMillilitres(rightMl);
            if (volume.IsFailure)
                return new Malformed(volume.Error!.Code);
            right = volume.Value;
        }

        var bySide = PumpYield.BySide(left, right);
        return bySide.IsSuccess ? Ok(new PumpDetails(bySide.Value)) : new Malformed(bySide.Error!.Code);
    }
}
