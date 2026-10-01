using Coo.Domain.CareLog;

namespace Coo.Codecs.Tests;

/// <summary>Old documents read correctly forever, and v1 output is stable (Spec 003 §9, ADR 0010 G6).</summary>
public sealed class GoldenFixtureTests
{
    public static TheoryData<string> FixtureNames => [.. Fixtures.Expected.Keys];

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Decode_EveryGoldenFixture_YieldsExpectedValue(string fixture)
    {
        // Arrange
        var codec = CodecFor(fixture);
        var json = Fixtures.Read(fixture);

        // Act
        var outcome = codec.Decode(json);

        // Assert
        var decoded = outcome.Should().BeOfType<Decoded>().Subject;
        decoded.Details.Should().Be(Fixtures.Expected[fixture]);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Encode_EveryGoldenFixture_ReproducesTheFixtureBytes(string fixture)
    {
        // Arrange
        var codec = CodecFor(fixture);
        var expected = Fixtures.Expected[fixture];

        // Act
        var json = codec.Encode(expected);

        // Assert
        json.Should().Be(Fixtures.Read(fixture));
    }

    [Fact]
    public void Manifest_EveryListedFixture_StillExists()
    {
        // Arrange
        var listed = File.ReadAllLines(Path.Combine(Fixtures.Directory, "fixtures-manifest.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l));

        // Act
        var onDisk = Fixtures.OnDisk().ToHashSet();

        // Assert
        listed.Should().OnlyContain(f => onDisk.Contains(f), "a released fixture is kept forever (ADR 0010 G6)");
    }

    [Fact]
    public void Manifest_EveryFixtureOnDisk_IsListedAndHasAnExpectation()
    {
        // Arrange
        var listed = File.ReadAllLines(Path.Combine(Fixtures.Directory, "fixtures-manifest.txt")).ToHashSet();

        // Act
        var onDisk = Fixtures.OnDisk().ToList();

        // Assert
        onDisk.Should().OnlyContain(f => listed.Contains(f) && Fixtures.Expected.ContainsKey(f));
    }

    [Fact]
    public void EveryCodecVersion_HasGoldenFixtures()
    {
        // Arrange
        var onDisk = Fixtures.OnDisk().ToList();

        // Act
        var missing = EntryDetailsCodecs.All
            .SelectMany(c => Enumerable.Range(1, c.CurrentVersion).Select(v => $"{c.Kind}/v{v}/"))
            .Where(prefix => !onDisk.Any(f => f.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        // Assert
        missing.Should().BeEmpty("every released version of every kind keeps fixtures (Spec 003 §9)");
    }

    [Fact]
    public void Decode_SameDocumentTwice_IsDeterministic()
    {
        // Arrange
        const string fixture = "feeding/v1/bottle-mixed.json";
        var codec = CodecFor(fixture);

        // Act
        var first = codec.Decode(Fixtures.Read(fixture));
        var second = codec.Decode(Fixtures.Read(fixture));

        // Assert
        first.Should().Be(second);
    }

    private static IEntryDetailsCodec CodecFor(string fixture) =>
        EntryDetailsCodecs.For(EntryKindCode.From(fixture.Split('/')[0]).Value)!;
}
