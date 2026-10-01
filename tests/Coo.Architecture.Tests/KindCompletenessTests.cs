using System.Reflection;
using Coo.Codecs;
using Coo.Domain.CareLog;
using NetArchTest.Rules;

namespace Coo.Architecture.Tests;

/// <summary>
/// ADR 0010 §4: every kind is registered in every layer that exists so far. A kind that is not registered
/// everywhere fails here, with the list of what is missing. Grows with each increment.
/// </summary>
public sealed class KindCompletenessTests
{
    [Fact]
    public void EveryKind_HasExactlyOneCodec()
    {
        // Arrange
        var kinds = EntryKinds.All.Select(k => k.Code).ToList();

        // Act
        var codecsPerKind = kinds.ToDictionary(k => k, k => EntryDetailsCodecs.All.Count(c => c.Kind == k));
        var orphanCodecs = EntryDetailsCodecs.All.Where(c => !kinds.Contains(c.Kind)).Select(c => c.Kind).ToList();

        // Assert
        codecsPerKind.Should().AllSatisfy(pair => pair.Value.Should().Be(1, $"kind '{pair.Key}' needs exactly one codec"));
        orphanCodecs.Should().BeEmpty("a codec must belong to a kind in the catalogue");
    }

    [Theory]
    [InlineData("Coo.Api.Application")]
    [InlineData("Coo.Mobile.Application")]
    [InlineData("Coo.Mobile.Presentation")]
    public void Application_Types_DoNotDependOnSystemTextJson(string assemblyName)
    {
        // Arrange
        var assembly = Assembly.Load(assemblyName);

        // Act
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn("System.Text.Json").GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty("only Coo.Codecs and Coo.Contracts touch JSON (Spec 003 §9)");
    }
}
