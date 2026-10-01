using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using FsCheck;
using FsCheck.Fluent;

namespace Coo.Codecs.Tests;

/// <summary>Generated round trips: 1,000 valid values per kind (Spec 003 §9, ADR 0010 G6).</summary>
public sealed class RoundTripPropertyTests
{
    private static readonly Config ThousandCases = Config.QuickThrowOnFailure.WithMaxTest(1000);

    public static TheoryData<string> Kinds => [.. EntryKinds.All.Select(k => k.Code.Value)];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void RoundTrip_GeneratedDetails_DecodeToEqualValue(string kind)
    {
        // Arrange
        var codec = EntryDetailsCodecs.For(EntryKindCode.From(kind).Value)!;
        var details = Arb.From(Generators.ByKind[kind]);

        // Act
        var property = Prop.ForAll(details, d => codec.Decode(codec.Encode(d)) is Decoded { Details: var back } && back.Equals(d));

        // Assert
        Check.One(ThousandCases, property);
    }

    [Fact]
    public void RoundTrip_GeneratedBreastFeeds_PreservesOffsets()
    {
        // Arrange
        var codec = EntryDetailsCodecs.For(FeedingKind.Instance.Code)!;
        var feeds = Arb.From(Generators.ByKind["feeding"].Where(d => d is BreastFeed));

        // Act
        var property = Prop.ForAll(feeds, d =>
        {
            var back = (BreastFeed)((Decoded)codec.Decode(codec.Encode(d))).Details;
            var original = ((BreastFeed)d).Segments;
            return back.Segments.Select(s => s.Start.Offset).SequenceEqual(original.Select(s => s.Start.Offset));
        });

        // Assert
        Check.One(ThousandCases, property);
    }

    [Fact]
    public void EveryKind_HasRoundTripGenerator()
    {
        // Arrange
        var kinds = EntryKinds.All.Select(k => k.Code.Value);

        // Act
        var missing = kinds.Where(k => !Generators.ByKind.ContainsKey(k)).ToList();

        // Assert
        missing.Should().BeEmpty("a new kind cannot skip the round-trip properties");
    }
}
