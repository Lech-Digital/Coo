using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;

namespace Coo.Codecs.Kinds;

/// <summary><c>diaper</c> v1 (Spec 003 §4.2).</summary>
internal sealed class DiaperCodec : EntryDetailsCodec<DiaperDetails, DiaperDetailsV1>
{
    public static readonly DiaperCodec Instance = new();

    private static readonly StringEnum<DiaperContents> Contents =
        new(("wet", DiaperContents.Wet), ("dirty", DiaperContents.Dirty), ("mixed", DiaperContents.Mixed));

    private DiaperCodec()
    {
    }

    public override EntryKindCode Kind => DiaperKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<DiaperDetailsV1> TypeInfo => CodecJson.Context.DiaperDetailsV1;

    protected override DiaperDetailsV1 ToDto(DiaperDetails details) => new() { V = 1, Contents = Contents.Code(details.Contents) };

    protected override DecodeOutcome FromDto(DiaperDetailsV1 dto) =>
        Contents.TryParse(dto.Contents, out var contents)
            ? Ok(new DiaperDetails(contents))
            : new Unrepresentable(DecodeReasons.UnknownValue("contents"));
}
