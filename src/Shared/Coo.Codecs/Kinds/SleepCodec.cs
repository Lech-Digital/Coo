using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Sleep;

namespace Coo.Codecs.Kinds;

/// <summary><c>sleep</c> v1 (Spec 003 §4.3).</summary>
internal sealed class SleepCodec : EntryDetailsCodec<SleepDetails, SleepDetailsV1>
{
    public static readonly SleepCodec Instance = new();

    private static readonly StringEnum<SleepKindOverride> Overrides =
        new(("nap", SleepKindOverride.Nap), ("night", SleepKindOverride.Night));

    private SleepCodec()
    {
    }

    public override EntryKindCode Kind => SleepKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<SleepDetailsV1> TypeInfo => CodecJson.Context.SleepDetailsV1;

    protected override SleepDetailsV1 ToDto(SleepDetails details) =>
        new() { V = 1, KindOverride = details.KindOverride is { } kind ? Overrides.Code(kind) : null };

    protected override DecodeOutcome FromDto(SleepDetailsV1 dto)
    {
        if (dto.KindOverride is null)
            return Ok(new SleepDetails());

        return Overrides.TryParse(dto.KindOverride, out var kind)
            ? Ok(new SleepDetails(kind))
            : new Unrepresentable(DecodeReasons.UnknownValue("kindOverride"));
    }
}
