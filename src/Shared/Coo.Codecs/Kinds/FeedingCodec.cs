using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;

namespace Coo.Codecs.Kinds;

/// <summary><c>feeding</c> v1 (Spec 003 §4.1).</summary>
internal sealed class FeedingCodec : EntryDetailsCodec<FeedingDetails, FeedingDetailsV1>
{
    public static readonly FeedingCodec Instance = new();

    private static readonly StringEnum<BreastSide> Sides = new(("left", BreastSide.Left), ("right", BreastSide.Right));

    private static readonly StringEnum<Milk> Milks = new(("expressedBreastMilk", Milk.ExpressedBreastMilk), ("formula", Milk.Formula));

    private FeedingCodec()
    {
    }

    public override EntryKindCode Kind => FeedingKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<FeedingDetailsV1> TypeInfo => CodecJson.Context.FeedingDetailsV1;

    protected override FeedingDetailsV1 ToDto(FeedingDetails details) => details switch
    {
        BreastFeed breast => new FeedingDetailsV1
        {
            V = 1,
            Method = "breast",
            Completed = breast.IsCompleted,
            Segments = [.. breast.Segments.Select(s => new BreastSegmentV1 { Side = Sides.Code(s.Side), Start = s.Start, End = s.End })],
        },
        BottleFeed bottle => new FeedingDetailsV1
        {
            V = 1,
            Method = "bottle",
            Components = [.. bottle.Components.Select(c => new BottleComponentV1 { Milk = Milks.Code(c.Milk), VolumeMl = Canonical(c.Volume.Millilitres) })],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(details), details.GetType().Name, "Unknown feeding method."),
    };

    protected override DecodeOutcome FromDto(FeedingDetailsV1 dto) => dto.Method switch
    {
        "breast" => ReadBreast(dto),
        "bottle" => ReadBottle(dto),
        _ => new Unrepresentable(DecodeReasons.UnknownValue("method")),
    };

    private static DecodeOutcome ReadBreast(FeedingDetailsV1 dto)
    {
        if (dto.Components is not null)
            return new Malformed(DecodeReasons.Unexpected("components"));
        if (dto.Completed is not { } completed)
            return new Malformed(DecodeReasons.Missing("completed"));
        if (dto.Segments is not { } segments)
            return new Malformed(DecodeReasons.Missing("segments"));

        var mapped = new List<BreastSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (!Sides.TryParse(segment.Side, out var side))
                return new Unrepresentable(DecodeReasons.UnknownValue("side"));
            mapped.Add(new BreastSegment(side, segment.Start, segment.End));
        }

        var feed = BreastFeed.Create(mapped, completed);
        return feed.IsSuccess ? Ok(feed.Value) : new Malformed(feed.Error!.Code);
    }

    private static DecodeOutcome ReadBottle(FeedingDetailsV1 dto)
    {
        if (dto.Completed is not null)
            return new Malformed(DecodeReasons.Unexpected("completed"));
        if (dto.Segments is not null)
            return new Malformed(DecodeReasons.Unexpected("segments"));
        if (dto.Components is not { } components)
            return new Malformed(DecodeReasons.Missing("components"));

        var mapped = new List<BottleComponent>(components.Count);
        foreach (var component in components)
        {
            if (!Milks.TryParse(component.Milk, out var milk))
                return new Unrepresentable(DecodeReasons.UnknownValue("milk"));

            var volume = Volume.FromMillilitres(component.VolumeMl);
            if (volume.IsFailure)
                return new Malformed(volume.Error!.Code);
            mapped.Add(new BottleComponent(milk, volume.Value));
        }

        var bottle = BottleFeed.Create(mapped);
        return bottle.IsSuccess ? Ok(bottle.Value) : new Malformed(bottle.Error!.Code);
    }
}
