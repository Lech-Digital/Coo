using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;

namespace Coo.Codecs.Tests;

public sealed class WriteGateAndUpcasterTests
{
    [Fact]
    public void Encode_CodecThatLosesData_ThrowsDetailsEncodingException()
    {
        // Arrange
        var codec = new LossyDiaperCodec();

        // Act
        var act = () => codec.Encode(new DiaperDetails(DiaperContents.Dirty));

        // Assert
        act.Should().Throw<DetailsEncodingException>();
    }

    [Fact]
    public void Encode_CodecThatLosesOffsets_ThrowsDetailsEncodingException()
    {
        // Arrange
        var codec = new OffsetLosingBreastCodec();
        var start = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.FromHours(2));
        var feed = BreastFeed.Create([new(BreastSide.Left, start, start.AddMinutes(10))], isCompleted: true).Value;

        // Act
        var act = () => codec.Encode(feed);

        // Assert
        act.Should().Throw<DetailsEncodingException>().WithMessage("*not stable*");
    }

    [Fact]
    public void Decode_TestKindV1_UpcastsToV2()
    {
        // Arrange
        var codec = new TestNoteCodec();

        // Act
        var outcome = codec.Decode("""{"v":1,"name":"first tooth"}""");

        // Assert
        outcome.Should().Be(new Decoded(new TestNoteDetails("first tooth"), StoredVersion: 1));
    }

    [Fact]
    public void Encode_TestKind_WritesCurrentVersion()
    {
        // Arrange
        var codec = new TestNoteCodec();

        // Act
        var json = codec.Encode(new TestNoteDetails("first tooth"), previous: """{"v":1,"name":"first tooth"}""");

        // Assert: previous is upcast before merging, so v1's "name" is not carried over as an unknown property.
        json.Should().Be("""{"v":2,"label":"first tooth"}""");
    }
}
