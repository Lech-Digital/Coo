using Coo.Domain.CareLog;

namespace Coo.Codecs.Tests;

/// <summary>Broken input never becomes a domain object; newer input is kept, not called broken (Spec 003 §5).</summary>
public sealed class DecodeOutcomeTests
{
    public static TheoryData<string, string, string> MalformedDocuments => new()
    {
        { "diaper", "not json", DecodeReasons.NotJson },
        { "diaper", "[1,2]", DecodeReasons.NotObject },
        { "diaper", """{"contents":"wet"}""", DecodeReasons.MissingVersion },
        { "diaper", """{"v":"1","contents":"wet"}""", DecodeReasons.MissingVersion },
        { "diaper", """{"v":0,"contents":"wet"}""", DecodeReasons.VersionInvalid },
        { "diaper", """{"v":1}""", "details.invalid:$" },
        { "diaper", """{"v":1,"contents":7}""", "details.invalid:$.contents" },
        { "feeding", """{"v":1,"method":"breast","completed":true}""", DecodeReasons.Missing("segments") },
        { "feeding", """{"v":1,"method":"breast","segments":[]}""", DecodeReasons.Missing("completed") },
        { "feeding", """{"v":1,"method":"breast","completed":true,"segments":[]}""", "breast.no-segments" },
        { "feeding", """{"v":1,"method":"breast","completed":true,"segments":[{"side":"left","start":"2026-09-30T14:00:00+02:00","end":"2026-09-30T14:10:00+02:00"},{"side":"right","start":"2026-09-30T14:05:00+02:00","end":"2026-09-30T14:20:00+02:00"}]}""", "breast.segments-overlap" },
        { "feeding", """{"v":1,"method":"breast","completed":true,"segments":[{"side":"left","start":"not a time"}]}""", "details.invalid:$.segments[0].start" },
        { "feeding", """{"v":1,"method":"breast","completed":true,"segments":[{"side":"left","start":"2026-09-30T14:00:00+02:00","end":"2026-09-30T14:10:00+02:00"}],"components":[]}""", DecodeReasons.Unexpected("components") },
        { "feeding", """{"v":1,"method":"bottle","components":[{"milk":"formula","volumeMl":"120"}]}""", "details.invalid:$.components[0].volumeMl" },
        { "feeding", """{"v":1,"method":"bottle","components":[{"milk":"formula","volumeMl":0}]}""", Volume.OutOfRange },
        { "feeding", """{"v":1,"method":"bottle","completed":true,"components":[{"milk":"formula","volumeMl":90}]}""", DecodeReasons.Unexpected("completed") },
        { "feeding", """{"v":1,"method":"bottle"}""", DecodeReasons.Missing("components") },
        { "pump", """{"v":1,"yield":{}}""", "pump.yield-empty" },
        { "pump", """{"v":1,"yield":{"leftMl":50,"totalMl":50}}""", DecodeReasons.Unexpected("totalMl") },
    };

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void Decode_MalformedDocument_ReturnsMalformedWithReason(string kind, string json, string reason)
    {
        // Arrange
        var codec = Codec(kind);

        // Act
        var outcome = codec.Decode(json);

        // Assert
        outcome.Should().Be(new Malformed(reason));
    }

    [Theory]
    [InlineData("feeding", """{"v":1,"method":"bottle","components":[{"milk":"donorMilk","volumeMl":90}]}""", "milk")]
    [InlineData("feeding", """{"v":1,"method":"solids"}""", "method")]
    [InlineData("feeding", """{"v":1,"method":"breast","completed":true,"segments":[{"side":"both","start":"2026-09-30T14:00:00+02:00","end":"2026-09-30T14:10:00+02:00"}]}""", "side")]
    [InlineData("diaper", """{"v":1,"contents":"lavender"}""", "contents")]
    [InlineData("sleep", """{"v":1,"kindOverride":"siesta"}""", "kindOverride")]
    [InlineData("milkbag", """{"v":1,"ml":120,"place":"fridge","container":"ice-tray"}""", "container")]
    public void Decode_UnknownEnumValue_ReturnsUnrepresentable(string kind, string json, string property)
    {
        // Arrange
        var codec = Codec(kind);

        // Act
        var outcome = codec.Decode(json);

        // Assert
        outcome.Should().Be(new Unrepresentable(DecodeReasons.UnknownValue(property)));
    }

    [Fact]
    public void Decode_VersionAboveCurrent_ReturnsUnrepresentable()
    {
        // Arrange: a document from a newer app, which this build has never seen.
        var codec = Codec("diaper");

        // Act
        var outcome = codec.Decode("""{"v":2,"contents":"wet","stool":{"colour":"yellow"}}""");

        // Assert
        outcome.Should().Be(new Unrepresentable(DecodeReasons.VersionTooNew));
    }

    [Fact]
    public void Decode_UnknownTopLevelProperty_StillDecodes()
    {
        // Arrange: an additive field from a newer app.
        var codec = Codec("diaper");

        // Act
        var outcome = codec.Decode("""{"v":1,"contents":"dirty","stoolColour":"yellow"}""");

        // Assert
        outcome.Should().BeOfType<Decoded>();
    }

    private static IEntryDetailsCodec Codec(string kind) => EntryDetailsCodecs.For(EntryKindCode.From(kind).Value)!;
}
