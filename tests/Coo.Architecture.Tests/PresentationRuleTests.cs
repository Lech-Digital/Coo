using System.Reflection;
using NetArchTest.Rules;

namespace Coo.Architecture.Tests;

/// <summary>Presentation stays plain net10.0, so view models and theme logic are testable without MAUI (Spec 005 §10).</summary>
public sealed class PresentationRuleTests
{
    [Fact]
    public void Presentation_Types_DoNotDependOnMaui()
    {
        // Arrange
        var assembly = Assembly.Load("Coo.Mobile.Presentation");

        // Act
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn("Microsoft.Maui").GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty();
    }
}
