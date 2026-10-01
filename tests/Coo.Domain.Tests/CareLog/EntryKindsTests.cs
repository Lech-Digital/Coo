using Coo.Domain.CareLog;

namespace Coo.Domain.Tests.CareLog;

/// <summary>The catalogue is open for extension and never loses a released code (Spec 001 §7.1).</summary>
public sealed class EntryKindsTests
{
    [Fact]
    public void All_EveryDetailsSubtype_BelongsToExactlyOneKind()
    {
        // Arrange
        var concreteDetails = typeof(EntryDetails).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(EntryDetails)) && !t.IsAbstract)
            .ToList();

        // Act
        var owners = concreteDetails.ToDictionary(t => t, t => EntryKinds.All.Count(k => k.DetailsType.IsAssignableFrom(t)));

        // Assert
        concreteDetails.Should().NotBeEmpty();
        owners.Should().AllSatisfy(pair => pair.Value.Should().Be(1, $"{pair.Key.Name} must belong to exactly one kind"));
    }

    [Fact]
    public void All_Codes_AreUnique()
    {
        // Arrange
        var codes = EntryKinds.All.Select(k => k.Code).ToList();

        // Act
        var distinct = codes.Distinct().Count();

        // Assert
        distinct.Should().Be(codes.Count);
    }

    [Fact]
    public void All_Codes_ContainEveryReleasedCode()
    {
        // Arrange
        var released = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "released-kind-codes.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Act
        var current = EntryKinds.All.Select(k => k.Code.Value).ToList();

        // Assert
        current.Should().Contain(released, "a released kind code is permanent (ADR 0010 §1)");
    }

    [Fact]
    public void Find_UnknownCode_ReturnsNull()
    {
        // Arrange
        var code = EntryKindCode.From("medication").Value;

        // Act
        var kind = EntryKinds.Find(code);

        // Assert
        kind.Should().BeNull();
    }
}
