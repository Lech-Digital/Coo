using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Diaper;
using Coo.Contracts.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;

namespace Coo.Codecs.Tests;

/// <summary>A test-only kind's details, never in the catalogue: proves the upcaster chain (Spec 003 §8).</summary>
public sealed record TestNoteDetails(string Label) : EntryDetails;

/// <summary>The test kind's v2 shape: v1 called the property <c>name</c>.</summary>
public sealed record TestNoteDetailsV2
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    public required string Label { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TestNoteDetailsV2))]
[JsonSerializable(typeof(DiaperDetailsV1))]
[JsonSerializable(typeof(FeedingDetailsV1))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

internal sealed class TestNoteCodec : EntryDetailsCodec<TestNoteDetails, TestNoteDetailsV2>
{
    public override EntryKindCode Kind { get; } = EntryKindCode.From("test-note").Value;

    public override int CurrentVersion => 2;

    protected override IReadOnlyList<Upcaster> Upcasters { get; } =
    [
        new(From: 1, Apply: v1 =>
        {
            var name = v1["name"]?.DeepClone();
            v1.Remove("name");
            v1["label"] = name;
            return v1;
        }),
    ];

    protected override JsonTypeInfo<TestNoteDetailsV2> TypeInfo => TestJsonContext.Default.TestNoteDetailsV2;

    protected override TestNoteDetailsV2 ToDto(TestNoteDetails details) => new() { V = 2, Label = details.Label };

    protected override DecodeOutcome FromDto(TestNoteDetailsV2 dto) => Ok(new TestNoteDetails(dto.Label));
}

/// <summary>A broken codec that loses data: every diaper is written as wet. The gate must refuse it.</summary>
internal sealed class LossyDiaperCodec : EntryDetailsCodec<DiaperDetails, DiaperDetailsV1>
{
    public override EntryKindCode Kind => DiaperKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<DiaperDetailsV1> TypeInfo => TestJsonContext.Default.DiaperDetailsV1;

    protected override DiaperDetailsV1 ToDto(DiaperDetails details) => new() { V = 1, Contents = "wet" };

    protected override DecodeOutcome FromDto(DiaperDetailsV1 dto) => Ok(new DiaperDetails(DiaperContents.Wet));
}

/// <summary>
/// A subtler broken codec: it writes instants in UTC. The instants stay equal, so value equality passes;
/// only the gate's byte comparison catches the lost offset (Spec 003 §6, step 5).
/// </summary>
internal sealed class OffsetLosingBreastCodec : EntryDetailsCodec<FeedingDetails, FeedingDetailsV1>
{
    public override EntryKindCode Kind => FeedingKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<FeedingDetailsV1> TypeInfo => TestJsonContext.Default.FeedingDetailsV1;

    protected override FeedingDetailsV1 ToDto(FeedingDetails details)
    {
        var breast = (BreastFeed)details;
        return new()
        {
            V = 1,
            Method = "breast",
            Completed = breast.IsCompleted,
            Segments = [.. breast.Segments.Select(s => new BreastSegmentV1
            {
                Side = s.Side == BreastSide.Left ? "left" : "right",
                Start = s.Start,
                End = s.End,
            })],
        };
    }

    protected override DecodeOutcome FromDto(FeedingDetailsV1 dto) =>
        Ok(BreastFeed.Create(
            dto.Segments!.Select(s => new BreastSegment(
                s.Side == "left" ? BreastSide.Left : BreastSide.Right,
                s.Start.ToUniversalTime(),
                s.End?.ToUniversalTime())),
            dto.Completed!.Value).Value);
}
