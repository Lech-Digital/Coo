namespace Coo.Architecture.Tests;

/// <summary>ADR 0001 §2 and §3, checked against the csproj files (Spec 002 §6).</summary>
public sealed class ProjectGraphTests
{
    // The allowed graph, exactly. A new project must be added here, so the graph never changes by accident.
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Coo.Domain"] = [],
        ["Coo.Contracts"] = [],
        ["Coo.Codecs"] = ["Coo.Contracts", "Coo.Domain"],
        ["Coo.Api.Application"] = ["Coo.Codecs", "Coo.Contracts", "Coo.Domain"],
        ["Coo.Api.Infrastructure"] = ["Coo.Api.Application"],
        ["Coo.Api"] = ["Coo.Api.Application", "Coo.Api.Infrastructure"],
        ["Coo.Mobile.Application"] = ["Coo.Codecs", "Coo.Contracts", "Coo.Domain"],
        ["Coo.Mobile.Infrastructure"] = ["Coo.Mobile.Application"],
        ["Coo.Mobile.Platform"] = ["Coo.Mobile.Application"],
        ["Coo.Mobile.Presentation"] = ["Coo.Mobile.Application"],
        ["Coo.Mobile"] = ["Coo.Mobile.Infrastructure", "Coo.Mobile.Platform", "Coo.Mobile.Presentation"],
    };

    private static readonly string[] AndroidProjects = ["Coo.Mobile", "Coo.Mobile.Platform"];

    [Fact]
    public void ProjectReferences_EveryProject_MatchesAllowedGraph()
    {
        // Arrange
        var projects = Repository.SourceProjects;

        // Act
        var actual = projects.ToDictionary(p => p.Name, p => p.ProjectReferences.ToArray());

        // Assert
        actual.Keys.Should().BeEquivalentTo(AllowedReferences.Keys,
            "every source project must be listed in the allowed graph, and every listed project must exist");
        foreach (var (name, references) in actual)
            references.Should().Equal(AllowedReferences[name], $"{name} may reference only its allowed projects");
    }

    [Theory]
    [InlineData("Coo.Domain")]
    [InlineData("Coo.Contracts")]
    [InlineData("Coo.Codecs")]
    public void PackageReferences_SharedCoreProject_ReferencesNoPackage(string projectName)
    {
        // Arrange
        var project = Repository.SourceProjects.Single(p => p.Name == projectName);

        // Act
        var packages = project.PackageReferences;

        // Assert
        packages.Should().BeEmpty($"{projectName} depends on the BCL only (ADR 0001 §2, CS-HEX-01)");
    }

    [Fact]
    public void ProjectReferences_Backend_NeverReferenceMobile()
    {
        // Arrange
        var mobileNames = Repository.SourceProjects.Where(p => p.IsMobile).Select(p => p.Name).ToHashSet();

        // Act
        var offending = Repository.SourceProjects
            .Where(p => p.IsBackend)
            .SelectMany(p => p.ProjectReferences.Where(mobileNames.Contains).Select(r => $"{p.Name} -> {r}"))
            .ToList();

        // Assert
        offending.Should().BeEmpty();
    }

    [Fact]
    public void ProjectReferences_Mobile_NeverReferenceBackend()
    {
        // Arrange
        var backendNames = Repository.SourceProjects.Where(p => p.IsBackend).Select(p => p.Name).ToHashSet();

        // Act
        var offending = Repository.SourceProjects
            .Where(p => p.IsMobile)
            .SelectMany(p => p.ProjectReferences.Where(backendNames.Contains).Select(r => $"{p.Name} -> {r}"))
            .ToList();

        // Assert
        offending.Should().BeEmpty();
    }

    [Fact]
    public void TargetFramework_OnlyPlatformAndHost_TargetAndroid()
    {
        // Arrange
        var projects = Repository.AllProjects;

        // Act
        var frameworks = projects.ToDictionary(p => p.Name, p => p.TargetFrameworks);

        // Assert
        foreach (var (name, tfms) in frameworks)
        {
            var expected = AndroidProjects.Contains(name) ? "net10.0-android" : "net10.0";
            tfms.Should().Equal([expected], $"{name} must single-target {expected} (ADR 0001 §3)");
        }
    }

    [Fact]
    public void TargetFramework_NoProject_MultiTargets()
    {
        // Arrange
        var projects = Repository.AllProjects;

        // Act
        var multiTargeted = projects.Where(p => p.IsMultiTargeted).Select(p => p.Name).ToList();

        // Assert
        multiTargeted.Should().BeEmpty("multi-targeting without the iOS workload breaks restore (NETSDK1147)");
    }
}
