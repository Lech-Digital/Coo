using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Health;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Health;

namespace Coo.Codecs.Kinds;

/// <summary><c>medicine</c> v1.</summary>
internal sealed class MedicineCodec : EntryDetailsCodec<MedicineDetails, MedicineDetailsV1>
{
    public static readonly MedicineCodec Instance = new();

    private MedicineCodec()
    {
    }

    public override EntryKindCode Kind => MedicineKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<MedicineDetailsV1> TypeInfo => CodecJson.Context.MedicineDetailsV1;

    protected override MedicineDetailsV1 ToDto(MedicineDetails details) => new() { V = 1, Name = details.Name, Dose = details.Dose };

    protected override DecodeOutcome FromDto(MedicineDetailsV1 dto)
    {
        var details = MedicineDetails.Create(dto.Name, dto.Dose);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("name"));
    }
}

/// <summary><c>temperature</c> v1.</summary>
internal sealed class TemperatureCodec : EntryDetailsCodec<TemperatureDetails, TemperatureDetailsV1>
{
    public static readonly TemperatureCodec Instance = new();

    private TemperatureCodec()
    {
    }

    public override EntryKindCode Kind => TemperatureKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<TemperatureDetailsV1> TypeInfo => CodecJson.Context.TemperatureDetailsV1;

    protected override TemperatureDetailsV1 ToDto(TemperatureDetails details) => new() { V = 1, Celsius = details.Celsius };

    protected override DecodeOutcome FromDto(TemperatureDetailsV1 dto)
    {
        var details = TemperatureDetails.Create(dto.Celsius);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("celsius"));
    }
}

/// <summary><c>vaccine</c> v1.</summary>
internal sealed class VaccineCodec : EntryDetailsCodec<VaccineDetails, VaccineDetailsV1>
{
    public static readonly VaccineCodec Instance = new();

    private VaccineCodec()
    {
    }

    public override EntryKindCode Kind => VaccineKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<VaccineDetailsV1> TypeInfo => CodecJson.Context.VaccineDetailsV1;

    protected override VaccineDetailsV1 ToDto(VaccineDetails details) => new() { V = 1, Name = details.Name };

    protected override DecodeOutcome FromDto(VaccineDetailsV1 dto)
    {
        var details = VaccineDetails.Create(dto.Name);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("name"));
    }
}

/// <summary><c>visit</c> v1.</summary>
internal sealed class VisitCodec : EntryDetailsCodec<VisitDetails, VisitDetailsV1>
{
    public static readonly VisitCodec Instance = new();

    private VisitCodec()
    {
    }

    public override EntryKindCode Kind => VisitKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<VisitDetailsV1> TypeInfo => CodecJson.Context.VisitDetailsV1;

    protected override VisitDetailsV1 ToDto(VisitDetails details) => new() { V = 1, With = details.With };

    protected override DecodeOutcome FromDto(VisitDetailsV1 dto)
    {
        var details = VisitDetails.Create(dto.With);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("with"));
    }
}
