using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.MilkStash;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;

namespace Coo.Codecs.Kinds;

/// <summary><c>milkbag</c> v1: the place and container as codes, the thaw as Unix milliseconds, days as <c>yyyy-MM-dd</c>.</summary>
internal sealed class MilkBagCodec : EntryDetailsCodec<MilkBagDetails, MilkBagDetailsV1>
{
    public static readonly MilkBagCodec Instance = new();

    private MilkBagCodec()
    {
    }

    public override EntryKindCode Kind => MilkBagKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<MilkBagDetailsV1> TypeInfo => CodecJson.Context.MilkBagDetailsV1;

    protected override MilkBagDetailsV1 ToDto(MilkBagDetails details) => new()
    {
        V = 1,
        Ml = Canonical(details.Volume.Millilitres),
        Place = details.Place == MilkPlace.Freezer ? "freezer" : "fridge",
        ThawedAtMs = details.ThawedAt?.ToUnixTimeMilliseconds(),
        UsedOn = Day(details.UsedOn),
        DiscardedOn = Day(details.DiscardedOn),
        Label = details.Label,
        Number = details.Number,
        Container = details.Container switch
        {
            MilkContainer.Bottle => "bottle",
            MilkContainer.Container => "container",
            _ => null,
        },
    };

    protected override DecodeOutcome FromDto(MilkBagDetailsV1 dto)
    {
        var volume = Volume.FromMillilitres(dto.Ml);
        if (volume.IsFailure)
            return new Malformed(volume.Error!.Code);
        MilkPlace? place = dto.Place switch
        {
            "fridge" => MilkPlace.Fridge,
            "freezer" => MilkPlace.Freezer,
            _ => null,
        };
        if (place is null)
            return new Malformed(DecodeReasons.Invalid("place"));
        if (!TryDay(dto.UsedOn, out var used))
            return new Malformed(DecodeReasons.Invalid("usedOn"));
        if (!TryDay(dto.DiscardedOn, out var discarded))
            return new Malformed(DecodeReasons.Invalid("discardedOn"));

        // A value this version does not know (a later container) is shown read-only, never guessed (ADR 0010 §3.3).
        MilkContainer? container = dto.Container switch
        {
            null or "bag" => MilkContainer.Bag,
            "bottle" => MilkContainer.Bottle,
            "container" => MilkContainer.Container,
            _ => null,
        };
        if (container is null)
            return new Unrepresentable(DecodeReasons.UnknownValue("container"));

        var thawed = dto.ThawedAtMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        var details = MilkBagDetails.Create(volume.Value, place.Value, thawed, used, discarded, dto.Label, dto.Number, container.Value);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(details.Error!.Code);
    }

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (text is null)
            return true;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = parsed;
        return true;
    }
}
