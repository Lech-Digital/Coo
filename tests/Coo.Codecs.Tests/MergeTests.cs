using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;

namespace Coo.Codecs.Tests;

/// <summary>An older app never erases what a newer app wrote, and never resurrects what a person cleared (Spec 003 §7).</summary>
public sealed class MergeTests
{
    [Fact]
    public void Encode_PreviousWithUnknownProperty_KeepsIt()
    {
        // Arrange: a newer app recorded the stool colour; this app changes the contents.
        var codec = EntryDetailsCodecs.For(new DiaperDetails(DiaperContents.Mixed));
        const string previous = """{"v":1,"contents":"dirty","stoolColour":"yellow"}""";

        // Act
        var json = codec.Encode(new DiaperDetails(DiaperContents.Mixed), previous);

        // Assert
        json.Should().Be("""{"v":1,"contents":"mixed","stoolColour":"yellow"}""");
    }

    [Fact]
    public void Encode_KnownPropertyClearedByEdit_IsNotResurrected()
    {
        // Arrange: the person removed the nap label.
        var codec = EntryDetailsCodecs.For(new SleepDetails());
        const string previous = """{"v":1,"kindOverride":"nap"}""";

        // Act
        var json = codec.Encode(new SleepDetails(), previous);

        // Assert
        json.Should().Be("""{"v":1}""");
    }

    [Fact]
    public void Encode_UnknownNestedProperty_IsNotMerged()
    {
        // Arrange: nested objects are closed; a field inside a component needs a new version.
        var bottle = BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(100).Value).Value;
        var codec = EntryDetailsCodecs.For(bottle);
        const string previous = """{"v":1,"method":"bottle","components":[{"milk":"formula","volumeMl":90,"brand":"X"}]}""";

        // Act
        var json = codec.Encode(bottle, previous);

        // Assert
        json.Should().Be("""{"v":1,"method":"bottle","components":[{"milk":"formula","volumeMl":100}]}""");
    }

    [Fact]
    public void Encode_MergedDocument_StillDecodesToTheValue()
    {
        // Arrange
        var details = new DiaperDetails(DiaperContents.Wet);
        var codec = EntryDetailsCodecs.For(details);

        // Act
        var json = codec.Encode(details, """{"v":1,"contents":"dirty","stoolColour":"green","consistency":"soft"}""");

        // Assert
        codec.Decode(json).Should().Be(new Decoded(details, StoredVersion: 1));
    }
}
