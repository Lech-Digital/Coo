using System.Text.Json;
using Coo.Contracts.Wearables;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>ADR 0009 §6: every golden fixture reads into the contract and writes back unchanged.</summary>
public sealed class WatchContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Entries => ["entry-diaper.json", "entry-breast-stopped.json", "entry-sleep-running.json", "entry-deleted.json"];

    [Theory]
    [MemberData(nameof(Entries))]
    public void EntryState_GoldenFixture_RoundTripsUnchanged(string file)
    {
        // Arrange
        var text = File.ReadAllText(Fixture(file)).Trim();

        // Act
        var state = JsonSerializer.Deserialize<WatchEntryStateV1>(text, Json)!;
        var written = JsonSerializer.Serialize(state, Json);

        // Assert
        state.SchemaVersion.Should().Be(WatchSchema.Current);
        Normalised(written).Should().Be(Normalised(text));
    }

    [Fact]
    public void Snapshot_GoldenFixture_RoundTripsUnchanged()
    {
        // Arrange
        var text = File.ReadAllText(Fixture("snapshot.json")).Trim();

        // Act
        var snapshot = JsonSerializer.Deserialize<WatchSnapshotV1>(text, Json)!;
        var written = JsonSerializer.Serialize(snapshot, Json);

        // Assert
        snapshot.Babies.Select(b => b.Name).Should().Equal("Laura", "Mia");
        Normalised(written).Should().Be(Normalised(text));
    }

    private static string Normalised(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private static string Fixture(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Coo.Tests.slnf")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "wearables", "contract-fixtures", file);
    }
}
