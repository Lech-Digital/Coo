using System.Text.Json;

namespace Coo.Architecture.Tests;

/// <summary>Coo.Tests.slnf never silently misses a project (Spec 002 §6).</summary>
public sealed class SolutionFilterTests
{
    private static readonly string[] AndroidProjects = ["Coo.Mobile", "Coo.Mobile.Platform"];

    [Fact]
    public void TestsSolutionFilter_ContainsEveryProjectExceptAndroidOnes()
    {
        // Arrange
        var filterPath = Path.Combine(Repository.Root.FullName, "Coo.Tests.slnf");
        using var filter = JsonDocument.Parse(File.ReadAllText(filterPath));
        var expected = Repository.AllProjects
            .Where(p => !AndroidProjects.Contains(p.Name))
            .Select(p => p.RelativePath)
            .ToList();

        // Act
        var listed = filter.RootElement.GetProperty("solution").GetProperty("projects")
            .EnumerateArray()
            .Select(e => e.GetString()!.Replace('\\', '/'))
            .ToList();

        // Assert
        listed.Should().BeEquivalentTo(expected);
    }
}
